using Microsoft.Extensions.Logging;
using Platform.Api.Authorization;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Catalog;

public sealed class CatalogNotificationAmbientTests
{
    [Fact]
    public async Task Order_created_with_null_ambient_does_not_notify_staff_across_tenants()
    {
        var tenantProvider = new FakeTenantProvider();
        await using var db = InMemoryAppDb.Create(tenantProvider);
        var tenantA = new Tenant("Ambient A", "66666666000191");
        var tenantB = new Tenant("Order B", "77777777000191");
        db.Tenants.AddRange(tenantA, tenantB);
        var staffA = new User(tenantA.Id, "staff-a", "Staff A", "staff-a@test.com");
        var staffB = new User(tenantB.Id, "staff-b", "Staff B", "staff-b@test.com");
        db.Users.AddRange(staffA, staffB);
        db.TenantNotificationChannelConfigs.AddRange(
            new TenantNotificationChannelConfig
            {
                TenantId = tenantA.Id,
                EventType = CatalogEventTypes.OrderCreated,
                Channel = NotificationChannel.Email,
                IsActive = true,
            },
            new TenantNotificationChannelConfig
            {
                TenantId = tenantB.Id,
                EventType = CatalogEventTypes.OrderCreated,
                Channel = NotificationChannel.InApp,
                IsActive = true,
            });
        await db.SaveChangesAsync();

        var permissions = new GrantingPermissionResolver();
        permissions.Grant(staffA.Id);
        permissions.Grant(staffB.Id);
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var publisher = new CatalogNotificationPublisher(
            db,
            tenantProvider,
            permissions,
            loggerFactory.CreateLogger<CatalogNotificationPublisher>());
        var order = new CatalogOrder
        {
            TenantId = tenantB.Id,
            CustomerId = Guid.NewGuid(),
            OrderNumber = 1,
            CustomerNameSnapshot = "Ana",
            CustomerEmailSnapshot = "ana@club.test",
        };

        tenantProvider.TenantId = null;
        var queued = await publisher.PublishOrderEventAsync(order, CatalogEventTypes.OrderCreated);
        await db.SaveChangesAsync();

        Assert.Empty(queued);
        Assert.Equal(0, permissions.Calls);
        Assert.Empty(db.Notifications.ToList());
        Assert.Empty(db.NotificationDeliveries.ToList());
        var warning = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("ambient tenant is missing", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Ana", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ana@club.test", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(order.CustomerId.ToString(), warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(tenantB.Id.ToString(), warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Order_created_channel_configs_are_limited_to_the_ambient_tenant_without_the_query_filter()
    {
        var dbTenant = new FakeTenantProvider();
        var ambient = new FakeTenantProvider();
        await using var db = InMemoryAppDb.Create(dbTenant);
        var tenantA = new Tenant("Filter A", "88888888000191");
        var tenantB = new Tenant("Filter B", "99999999000191");
        db.Tenants.AddRange(tenantA, tenantB);
        db.TenantNotificationChannelConfigs.AddRange(
            new TenantNotificationChannelConfig
            {
                TenantId = tenantA.Id,
                EventType = CatalogEventTypes.OrderCreated,
                Channel = NotificationChannel.Email,
                IsActive = true,
            },
            new TenantNotificationChannelConfig
            {
                TenantId = tenantB.Id,
                EventType = CatalogEventTypes.OrderCreated,
                Channel = NotificationChannel.InApp,
                IsActive = true,
            });
        await db.SaveChangesAsync();

        dbTenant.TenantId = null;
        ambient.TenantId = tenantB.Id;
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var publisher = new CatalogNotificationPublisher(
            db,
            ambient,
            new GrantingPermissionResolver(),
            loggerFactory.CreateLogger<CatalogNotificationPublisher>());
        var order = new CatalogOrder
        {
            TenantId = tenantB.Id,
            CustomerId = Guid.NewGuid(),
            OrderNumber = 7,
            CustomerNameSnapshot = "Bruno",
            CustomerEmailSnapshot = "bruno@club.test",
        };

        await publisher.PublishOrderEventAsync(order, CatalogEventTypes.OrderCreated);
        await db.SaveChangesAsync();

        var deliveries = db.NotificationDeliveries.ToList();
        var delivery = Assert.Single(deliveries);
        Assert.Equal(NotificationChannel.InApp, delivery.Channel);
        Assert.Equal(tenantB.Id, delivery.TenantId);
        Assert.Equal(NotificationRecipientKind.Customer, delivery.RecipientKind);
    }

    [Fact]
    public async Task Order_created_notifies_ambient_staff_with_orders_manage()
    {
        await using var harness = await CatalogHarness.CreateAsync();
        var staff = harness.AddManageUser();
        var product = await harness.Products.CreateAsync(
            new CreateCatalogProductRequest { Name = "Item", Price = 1m },
            CancellationToken.None);
        var customer = harness.AddCustomer();
        await harness.Db.SaveChangesAsync();

        await harness.Portal.CreateOrderAsync(
            customer.Id,
            new CreatePortalOrderRequest
            {
                Items = [new CreatePortalOrderItemRequest { ProductId = product.Id, Quantity = 1 }],
            },
            CancellationToken.None);

        var staffDeliveries = harness.Db.NotificationDeliveries
            .Where(delivery => delivery.RecipientKind == NotificationRecipientKind.B2BUser)
            .ToList();
        Assert.Contains(staffDeliveries, delivery => delivery.RecipientId == staff.Id);
        Assert.All(
            staffDeliveries,
            delivery => Assert.Equal(harness.Tenant.Id, delivery.TenantId));
    }

    private sealed class GrantingPermissionResolver : IPermissionResolver
    {
        private readonly HashSet<Guid> _granted = [];

        public int Calls { get; private set; }

        public void Grant(Guid userId) => _granted.Add(userId);

        public Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(
            Guid tenantId,
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            IReadOnlySet<string> keys = _granted.Contains(userId)
                ? new HashSet<string>(StringComparer.Ordinal) { Permissions.Catalog.OrdersManage }
                : new HashSet<string>(StringComparer.Ordinal);
            return Task.FromResult(keys);
        }

        public async Task<bool> HasPermissionAsync(
            Guid tenantId,
            Guid userId,
            string permissionKey,
            CancellationToken cancellationToken = default)
        {
            var effective = await GetEffectivePermissionsAsync(tenantId, userId, cancellationToken);
            return effective.Contains(permissionKey);
        }

        public Task<IReadOnlySet<string>> GetAdminWildcardKeysAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));
    }
}
