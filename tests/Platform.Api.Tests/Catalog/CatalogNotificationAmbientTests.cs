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
        db.TenantNotificationChannelConfigs.Add(new TenantNotificationChannelConfig
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
        var publisher = new CatalogNotificationPublisher(db, tenantProvider, permissions);
        var order = new CatalogOrder
        {
            TenantId = tenantB.Id,
            CustomerId = Guid.NewGuid(),
            OrderNumber = 1,
            CustomerNameSnapshot = "Ana",
            CustomerEmailSnapshot = "ana@club.test",
        };

        tenantProvider.TenantId = null;
        await publisher.PublishOrderEventAsync(order, CatalogEventTypes.OrderCreated);
        await db.SaveChangesAsync();

        var deliveries = db.NotificationDeliveries.ToList();
        Assert.Equal(0, permissions.Calls);
        Assert.DoesNotContain(deliveries, delivery => delivery.RecipientKind == NotificationRecipientKind.B2BUser);
        Assert.DoesNotContain(deliveries, delivery => delivery.RecipientId == staffA.Id);
        Assert.DoesNotContain(deliveries, delivery => delivery.RecipientId == staffB.Id);
        Assert.Contains(
            deliveries,
            delivery => delivery.RecipientKind == NotificationRecipientKind.Customer
                && delivery.RecipientId == order.CustomerId);
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
