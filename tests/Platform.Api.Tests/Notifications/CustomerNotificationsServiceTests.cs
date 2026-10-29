using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Platform.Api.Authentication;
using Platform.Api.Modules.Notifications.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Notifications;

public sealed class CustomerNotificationsServiceTests
{
    [Fact]
    public async Task Every_operation_is_scoped_to_customer_tenant_kind_and_inapp_channel()
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider());
        var tenant = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var principal = Principal(tenant, customer);
        var own = Seed(db, tenant, customer);
        var foreignCustomer = Seed(db, tenant, Guid.NewGuid());
        var foreignTenant = Seed(db, Guid.NewGuid(), customer);
        var staff = Seed(db, tenant, customer, kind: NotificationRecipientKind.B2BUser);
        var email = Seed(db, tenant, customer, channel: NotificationChannel.Email);
        var mismatchedNotification = Seed(db, tenant, customer);
        mismatchedNotification.Notification.TenantId = Guid.NewGuid();
        await db.SaveChangesAsync();
        var service = new CustomerNotificationsService(db);

        var summary = await service.GetSummaryAsync(principal, CancellationToken.None);
        Assert.Equal(1, summary.UnreadTotal);
        Assert.Equal(1, Assert.Single(summary.Groups).Count);
        var list = await service.ListAsync(principal, null, false, 20, null, CancellationToken.None);
        Assert.Equal(own.Id, Assert.Single(list.Items).Id);

        foreach (var foreign in new[] { foreignCustomer, foreignTenant, staff, email, mismatchedNotification })
        {
            Assert.Null(await service.MarkReadAsync(principal, foreign.Id, CancellationToken.None));
        }

        Assert.Equal(1, await service.MarkAllReadAsync(principal, CancellationToken.None));
        Assert.Equal(0, (await service.GetSummaryAsync(principal, CancellationToken.None)).UnreadTotal);
        Assert.All(new[] { foreignCustomer, foreignTenant, staff, email, mismatchedNotification }, d => Assert.Null(d.ReadAt));
        Assert.Equal(0, await service.MarkAllReadAsync(principal, CancellationToken.None));
    }

    [Fact]
    public async Task Pagination_is_newest_first_stable_and_read_filter_and_rendering_work()
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider());
        var tenant = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var principal = Principal(tenant, customer);
        db.NotificationTemplates.Add(new NotificationTemplate
        {
            EventType = CatalogEventTypes.OrderApproved,
            Channel = NotificationChannel.InApp,
            Language = "pt-BR",
            BodyTemplate = "Pedido {{orderNumber}} aprovado.",
            IsActive = true,
        });
        var deliveries = Enumerable.Range(0, 5).Select(_ => Seed(db, tenant, customer)).ToList();
        // Equal timestamps exercise the ID tie-breaker, including across page boundaries.
        var createdAt = DateTimeOffset.Parse("2026-10-29T12:00:00Z");
        foreach (var delivery in deliveries)
        {
            db.Entry(delivery.Notification).Property(n => n.CreatedAt).CurrentValue = createdAt;
        }
        await db.SaveChangesAsync();
        var service = new CustomerNotificationsService(db);
        var expected = deliveries.OrderByDescending(d => d.Notification.CreatedAt).ThenByDescending(d => d.Id).Select(d => d.Id).ToArray();
        var first = await service.ListAsync(principal, null, false, 2, "pt-BR", CancellationToken.None);
        var second = await service.ListAsync(principal, first.NextCursor, false, 2, null, CancellationToken.None);
        var third = await service.ListAsync(principal, second.NextCursor, false, 2, null, CancellationToken.None);
        Assert.Equal(expected, first.Items.Concat(second.Items).Concat(third.Items).Select(i => i.Id));
        Assert.Null(third.NextCursor);
        Assert.All(first.Items, item =>
        {
            Assert.Equal("Pedido #12 aprovado.", item.Title);
            Assert.Equal("/t/example/pedidos/12", item.TargetRoute);
            Assert.Equal("catalog", item.Module);
        });
        Assert.Equal("/t/example/pedidos/12", Assert.Single((await service.GetSummaryAsync(principal, CancellationToken.None)).Groups).TargetRoute);

        Assert.Equal(1, await service.MarkReadAsync(principal, expected[0], CancellationToken.None));
        var readAt = deliveries.Single(d => d.Id == expected[0]).ReadAt;
        Assert.NotNull(readAt);
        Assert.Equal(0, await service.MarkReadAsync(principal, expected[0], CancellationToken.None));
        Assert.Equal(readAt, deliveries.Single(d => d.Id == expected[0]).ReadAt);
        var unread = await service.ListAsync(principal, null, true, 20, null, CancellationToken.None);
        Assert.Equal(4, unread.Items.Count);
        Assert.DoesNotContain(unread.Items, i => i.Id == expected[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mark_read_preserves_a_read_saved_by_another_context(bool readAll)
    {
        var databaseName = $"customer-inbox-{Guid.NewGuid():N}";
        await using var staleDb = InMemoryAppDb.Create(new FakeTenantProvider(), databaseName);
        var tenant = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var principal = Principal(tenant, customer);
        var delivery = Seed(staleDb, tenant, customer);
        await staleDb.SaveChangesAsync();

        await using var otherDb = InMemoryAppDb.Create(new FakeTenantProvider(), databaseName);
        var otherService = new CustomerNotificationsService(otherDb);
        Assert.Equal(1, await otherService.MarkReadAsync(principal, delivery.Id, CancellationToken.None));
        var original = Assert.Single((await otherService.ListAsync(principal, null, false, 20, null, CancellationToken.None)).Items);
        Assert.NotNull(original.ReadAt);

        var staleService = new CustomerNotificationsService(staleDb);
        var updated = readAll
            ? await staleService.MarkAllReadAsync(principal, CancellationToken.None)
            : await staleService.MarkReadAsync(principal, delivery.Id, CancellationToken.None);

        Assert.Equal(0, updated);
        var current = Assert.Single((await staleService.ListAsync(principal, null, false, 20, null, CancellationToken.None)).Items);
        Assert.Equal(original.ReadAt, current.ReadAt);
    }

    [Fact]
    public async Task Summary_counts_unread_groups_and_uses_each_groups_latest_delivery()
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider());
        var tenant = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var principal = Principal(tenant, customer);
        var older = Seed(db, tenant, customer);
        var latest = Seed(db, tenant, customer);
        var ready = Seed(db, tenant, customer);
        ready.Notification.EventType = CatalogEventTypes.OrderReady;
        ready.Notification.Payload["module"] = "custom-module";
        var read = Seed(db, tenant, customer);
        read.MarkRead(DateTimeOffset.UtcNow);
        var createdAt = DateTimeOffset.Parse("2026-10-29T12:00:00Z");
        foreach (var delivery in new[] { older, latest, ready, read })
        {
            db.Entry(delivery.Notification).Property(n => n.CreatedAt).CurrentValue = createdAt;
            delivery.Notification.Payload["targetRoute"] = $"/orders/{delivery.Id}";
        }
        db.Entry(older.Notification).Property(n => n.CreatedAt).CurrentValue = createdAt.AddMinutes(-1);
        await db.SaveChangesAsync();

        var summary = await new CustomerNotificationsService(db).GetSummaryAsync(principal, CancellationToken.None);

        Assert.Equal(3, summary.UnreadTotal);
        Assert.Equal(2, summary.Groups.Count);
        var approved = Assert.Single(summary.Groups, g => g.EventType == CatalogEventTypes.OrderApproved);
        Assert.Equal(2, approved.Count);
        Assert.Equal("catalog", approved.Module);
        Assert.Equal(createdAt, approved.LatestAt);
        Assert.Equal($"/orders/{latest.Id}", approved.TargetRoute);
        var readyGroup = Assert.Single(summary.Groups, g => g.EventType == CatalogEventTypes.OrderReady);
        Assert.Equal(1, readyGroup.Count);
        Assert.Equal("custom-module", readyGroup.Module);
        Assert.Equal($"/orders/{ready.Id}", readyGroup.TargetRoute);
        Assert.Equal(new[] { latest, ready }.OrderByDescending(d => d.Id).Select(d => d.Notification.EventType),
            summary.Groups.Select(g => g.EventType));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Overlapping_reads_count_each_delivery_once(bool readAll)
    {
        var databaseName = $"customer-inbox-{Guid.NewGuid():N}";
        var pause = new PauseSaveInterceptor();
        await using var firstDb = InMemoryAppDb.Create(new FakeTenantProvider(), databaseName, interceptors: [pause]);
        await using var secondDb = InMemoryAppDb.Create(new FakeTenantProvider(), databaseName);
        var tenant = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var principal = Principal(tenant, customer);
        var delivery = Seed(secondDb, tenant, customer);
        await secondDb.SaveChangesAsync();

        var firstRead = new CustomerNotificationsService(firstDb).MarkReadAsync(principal, delivery.Id, CancellationToken.None);
        Task<int?> secondRead;
        try
        {
            await pause.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var secondService = new CustomerNotificationsService(secondDb);
            secondRead = readAll
                ? ReadAllAsync()
                : secondService.MarkReadAsync(principal, delivery.Id, CancellationToken.None);

            async Task<int?> ReadAllAsync() => await secondService.MarkAllReadAsync(principal, CancellationToken.None);
        }
        finally
        {
            pause.Resume.TrySetResult();
        }

        Assert.Equal(new int?[] { 1, 0 }, await Task.WhenAll(firstRead, secondRead));
        Assert.Equal(0, (await new CustomerNotificationsService(secondDb).GetSummaryAsync(principal, CancellationToken.None)).UnreadTotal);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(null, 51)]
    [InlineData("not-base64", 20)]
    [InlineData("OTIyMzM3MjAzNjg1NDc3NTgwNzowMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMA==", 20)]
    public async Task Invalid_pagination_is_rejected(string? cursor, int limit)
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider());
        var service = new CustomerNotificationsService(db);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListAsync(Principal(Guid.NewGuid(), Guid.NewGuid()), cursor, false, limit, null, CancellationToken.None));
    }

    [Fact]
    public async Task Missing_claims_or_staff_identity_fail_closed()
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider());
        var service = new CustomerNotificationsService(db);
        foreach (var principal in new[]
        {
            new ClaimsPrincipal(),
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, AuthRoles.Customer)], "test")),
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(CustomerClaimTypes.CustomerId, Guid.NewGuid().ToString()), new Claim(CustomerClaimTypes.TenantId, Guid.NewGuid().ToString())], "test")),
        })
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetSummaryAsync(principal, CancellationToken.None));
        }
    }

    internal static ClaimsPrincipal Principal(Guid tenant, Guid customer) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.Role, AuthRoles.Customer),
        new Claim(CustomerClaimTypes.CustomerId, customer.ToString()),
        new Claim(CustomerClaimTypes.TenantId, tenant.ToString()),
    ], CustomerJwtBearerDefaults.AuthenticationScheme));

    private sealed class PauseSaveInterceptor : SaveChangesInterceptor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    internal static NotificationDelivery Seed(AppDbContext db, Guid tenant, Guid customer,
        NotificationRecipientKind kind = NotificationRecipientKind.Customer,
        NotificationChannel channel = NotificationChannel.InApp)
    {
        var notification = new Notification
        {
            TenantId = tenant,
            EventType = CatalogEventTypes.OrderApproved,
            AggregateType = "CatalogOrder",
            AggregateId = Guid.NewGuid(),
            Payload = new() { ["orderNumber"] = "#12", ["targetRoute"] = "/t/example/pedidos/12" },
        };
        var delivery = new NotificationDelivery
        {
            TenantId = tenant, NotificationId = notification.Id, Channel = channel,
            RecipientKind = kind, RecipientId = customer, RecipientEmail = "same@example.test",
        };
        delivery.MarkDelivered();
        notification.AddDelivery(delivery);
        db.Notifications.Add(notification);
        return delivery;
    }
}
