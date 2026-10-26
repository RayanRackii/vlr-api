using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Modules.Rentals;
using Platform.Api.DomainEvents;
using Platform.Api.Modules.Catalog;
using Platform.Api.Tests.Catalog;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Domain.Events;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.DomainEvents;

public sealed class DomainEventOutboxTests(PostgresContainerFixture postgres)
    : IClassFixture<PostgresContainerFixture>
{
    [DockerFact]
    public Task Order_and_outbox_are_committed_together() => VerifyTransactionAsync(commit: true);

    [DockerFact]
    public Task Rollback_discards_order_change_and_outbox() => VerifyTransactionAsync(commit: false);

    private async Task VerifyTransactionAsync(bool commit)
    {
        Assert.NotNull(postgres.Factory);
        var tenant = new Tenant("Event Club", Guid.NewGuid().ToString("N")[..14]);
        var tenantProvider = new FakeTenantProvider { TenantId = tenant.Id };
        await using var db = postgres.Factory.Create(tenantProvider);
        db.Tenants.Add(tenant);
        var customer = new Customer { TenantId = tenant.Id, Name = "Ana", Email = "ana@club.test" };
        db.Customers.Add(customer);
        var order = new CatalogOrder
        {
            TenantId = tenant.Id,
            CustomerId = customer.Id,
            OrderNumber = 1,
            CustomerNameSnapshot = "Ana",
            CustomerEmailSnapshot = customer.Email,
        };
        db.CatalogOrders.Add(order);
        await db.SaveChangesAsync();

        // Resolve the production dispatcher/handler registrations with the caller's DbContext.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(db);
        services.AddSingleton<ITenantProvider>(tenantProvider);
        services.AddSingleton<Platform.Api.Authorization.IPermissionResolver>(new FakePermissionResolver());
        services.AddCatalogModule();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var events = scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher<CatalogOrderEvent>>();
        await events.EnsureReadyAsync();
        var emailConfig = await db.TenantNotificationChannelConfigs.SingleAsync(c =>
            c.EventType == CatalogEventTypes.OrderApproved && c.Channel == NotificationChannel.Email);
        emailConfig.IsActive = true;
        await db.SaveChangesAsync();

        await using var transaction = await db.Database.BeginTransactionAsync();
        order.Approve(CatalogActorType.System, null);
        var queued = await events.DispatchAsync(new CatalogOrderEvent(order, CatalogEventTypes.OrderApproved));
        await db.SaveChangesAsync();

        var pending = await db.Notifications.Include(n => n.Deliveries)
            .SingleAsync(n => n.AggregateId == order.Id);
        Assert.Equal(tenant.Id, pending.TenantId);
        Assert.Equal("catalog.order.approved", pending.EventType);
        Assert.Equal("CatalogOrder", pending.AggregateType);
        var email = Assert.Single(pending.Deliveries, d => d.Channel == NotificationChannel.Email);
        Assert.Equal(email.Id, Assert.Single(queued));
        Assert.Equal(NotificationDeliveryStatus.Queued, email.Status);
        Assert.Equal(tenant.Id, email.TenantId);

        await using (var outside = postgres.Factory.Create(tenantProvider))
        {
            Assert.Empty(await outside.Notifications.Where(n => n.AggregateId == order.Id).ToListAsync());
            Assert.Equal(CatalogOrderStatus.Requested,
                await outside.CatalogOrders.Where(o => o.Id == order.Id).Select(o => o.Status).SingleAsync());
        }

        if (commit)
            await transaction.CommitAsync();
        else
            await transaction.RollbackAsync();

        await using var verify = postgres.Factory.Create(tenantProvider);
        Assert.Equal(commit ? CatalogOrderStatus.Approved : CatalogOrderStatus.Requested,
            await verify.CatalogOrders.Where(o => o.Id == order.Id).Select(o => o.Status).SingleAsync());
        Assert.Equal(commit ? 1 : 0,
            await verify.Notifications.CountAsync(n => n.AggregateId == order.Id));
        Assert.Equal(commit ? 2 : 0,
            await verify.NotificationDeliveries.CountAsync(d => d.NotificationId == pending.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reservation_event_preserves_outbox_and_tenant_guard(bool mismatchedTenant)
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(harness.Db);
        services.AddSingleton<ITenantProvider>(harness.TenantProvider);
        services.AddRentalsModule();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var events = scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher<ReservationEvent>>();
        await events.EnsureReadyAsync();
        var reservation = new Reservation
        {
            TenantId = mismatchedTenant ? Guid.NewGuid() : harness.TenantId,
            UnitId = harness.UnitId,
            CustomerId = harness.CustomerId,
            CustomerName = "Ana",
            CustomerWhatsApp = "+5511999999999",
            StartDateTime = LocationBookingHarness.RangeStart,
            EndDateTime = LocationBookingHarness.RangeEnd,
            Status = ReservationStatus.Confirmed,
            TotalAmount = 100m,
            DepositPaid = 0m,
        };
        var queued = await events.DispatchAsync(new ReservationEvent(reservation, RentalEventTypes.ReservationConfirmed));
        await harness.Db.SaveChangesAsync();
        if (mismatchedTenant)
        {
            Assert.Empty(queued);
            Assert.Empty(await harness.Db.Notifications.ToListAsync());
            Assert.Empty(await harness.Db.NotificationDeliveries.ToListAsync());
            return;
        }

        var notification = await harness.Db.Notifications.Include(n => n.Deliveries).SingleAsync();
        Assert.Equal(reservation.Id, notification.AggregateId);
        Assert.Equal(harness.TenantId, notification.TenantId);
        Assert.Equal("rentals.reservation.confirmed", notification.EventType);
        var email = Assert.Single(notification.Deliveries, d => d.Channel == NotificationChannel.Email);
        Assert.Equal(NotificationDeliveryStatus.Queued, email.Status);
        Assert.Equal(email.Id, Assert.Single(queued));
    }

    [Fact]
    public async Task Dispatch_without_save_does_not_persist_the_outbox()
    {
        await using var harness = await CatalogHarness.CreateAsync();
        var events = new DomainEventDispatcher<CatalogOrderEvent>([harness.Publisher]);
        await events.EnsureReadyAsync();
        var order = new CatalogOrder
        {
            TenantId = harness.Tenant.Id,
            CustomerId = Guid.NewGuid(),
            CustomerNameSnapshot = "Ana",
            OrderNumber = 1,
        };
        await events.DispatchAsync(new CatalogOrderEvent(order, CatalogEventTypes.OrderApproved));
        Assert.Empty(await harness.Db.Notifications.ToListAsync());
        await harness.Db.SaveChangesAsync();
        Assert.Equal(order.Id, (await harness.Db.Notifications.SingleAsync()).AggregateId);
    }
}
