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
    public async Task Order_event_with_mismatched_tenant_does_not_publish()
    {
        var dbTenant = new FakeTenantProvider();
        var ambient = new FakeTenantProvider();
        await using var db = InMemoryAppDb.Create(dbTenant);
        var tenantA = new Tenant("Guard A", "12121212000191");
        var tenantB = new Tenant("Guard B", "13131313000191");
        db.Tenants.AddRange(tenantA, tenantB);
        db.TenantNotificationChannelConfigs.AddRange(
            new TenantNotificationChannelConfig
            {
                TenantId = tenantA.Id,
                EventType = CatalogEventTypes.OrderCreated,
                Channel = NotificationChannel.InApp,
                IsActive = true,
            },
            new TenantNotificationChannelConfig
            {
                TenantId = tenantB.Id,
                EventType = CatalogEventTypes.OrderCreated,
                Channel = NotificationChannel.Email,
                IsActive = true,
            });
        await db.SaveChangesAsync();

        dbTenant.TenantId = null;
        ambient.TenantId = tenantA.Id;
        var permissions = new GrantingPermissionResolver();
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var publisher = new CatalogNotificationPublisher(
            db,
            ambient,
            permissions,
            loggerFactory.CreateLogger<CatalogNotificationPublisher>());
        var customerId = Guid.NewGuid();
        var order = new CatalogOrder
        {
            TenantId = tenantB.Id,
            CustomerId = customerId,
            OrderNumber = 4,
            CustomerNameSnapshot = "Ana",
            CustomerEmailSnapshot = "ana@club.test",
        };

        var queued = await publisher.PublishOrderEventAsync(order, CatalogEventTypes.OrderCreated);
        await db.SaveChangesAsync();

        Assert.Empty(queued);
        Assert.Equal(0, permissions.Calls);
        Assert.Empty(db.Notifications.ToList());
        Assert.Empty(db.NotificationDeliveries.ToList());
        var warning = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("does not match the ambient tenant", warning.Message, StringComparison.Ordinal);
        Assert.Contains(CatalogEventTypes.OrderCreated, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Ana", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ana@club.test", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(customerId.ToString(), warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(order.Id.ToString(), warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(tenantA.Id.ToString(), warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(tenantB.Id.ToString(), warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Channel_defaults_seed_only_the_ambient_tenant_when_the_query_filter_is_off()
    {
        var dbTenant = new FakeTenantProvider();
        var ambient = new FakeTenantProvider();
        await using var db = InMemoryAppDb.Create(dbTenant);
        var tenantA = new Tenant("Seed A", "14141414000191");
        var tenantB = new Tenant("Seed B", "15151515000191");
        db.Tenants.AddRange(tenantA, tenantB);

        foreach (var eventType in CatalogEventTypes.Notifying)
        {
            foreach (var channel in DefaultChannels)
            {
                if (eventType == CatalogEventTypes.OrderCreated
                    && channel == NotificationChannel.Email)
                {
                    continue;
                }

                db.TenantNotificationChannelConfigs.Add(new TenantNotificationChannelConfig
                {
                    TenantId = tenantA.Id,
                    EventType = eventType,
                    Channel = channel,
                    IsActive = false,
                });
            }
        }

        await db.SaveChangesAsync();

        var foreignBefore = db.TenantNotificationChannelConfigs
            .Where(config => config.TenantId == tenantA.Id)
            .Select(config => new { config.Id, config.EventType, config.Channel, config.IsActive })
            .ToList();

        db.TenantNotificationChannelConfigs.Add(new TenantNotificationChannelConfig
        {
            TenantId = tenantA.Id,
            EventType = CatalogEventTypes.OrderCreated,
            Channel = NotificationChannel.Email,
            IsActive = true,
        });

        dbTenant.TenantId = null;
        ambient.TenantId = tenantB.Id;
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var publisher = new CatalogNotificationPublisher(
            db,
            ambient,
            new GrantingPermissionResolver(),
            loggerFactory.CreateLogger<CatalogNotificationPublisher>());

        await publisher.EnsureReadyAsync();

        var configs = db.TenantNotificationChannelConfigs.ToList();
        var foreign = configs.Where(config => config.TenantId == tenantA.Id).ToList();
        var seeded = configs.Where(config => config.TenantId == tenantB.Id).ToList();

        Assert.Equal(foreignBefore.Count + 1, foreign.Count);
        Assert.All(foreignBefore, before =>
        {
            var after = Assert.Single(
                foreign,
                config => config.Id == before.Id);
            Assert.Equal(before.EventType, after.EventType);
            Assert.Equal(before.Channel, after.Channel);
            Assert.Equal(before.IsActive, after.IsActive);
        });
        var foreignEmail = Assert.Single(
            foreign,
            config => config.EventType == CatalogEventTypes.OrderCreated
                && config.Channel == NotificationChannel.Email);
        Assert.True(foreignEmail.IsActive);
        Assert.Equal(CatalogEventTypes.Notifying.Length * DefaultChannels.Length, seeded.Count);
        Assert.All(
            seeded.Where(config => config.Channel == NotificationChannel.InApp),
            config => Assert.True(config.IsActive));
        Assert.All(
            seeded.Where(config => config.Channel != NotificationChannel.InApp),
            config => Assert.False(config.IsActive));
        Assert.Contains(
            seeded,
            config => config.EventType == CatalogEventTypes.OrderCreated
                && config.Channel == NotificationChannel.Email
                && !config.IsActive);
        Assert.DoesNotContain(configs, config => config.TenantId != tenantA.Id && config.TenantId != tenantB.Id);
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

    private static readonly NotificationChannel[] DefaultChannels =
    [
        NotificationChannel.InApp,
        NotificationChannel.Email,
        NotificationChannel.WhatsApp,
        NotificationChannel.Sms,
    ];

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
