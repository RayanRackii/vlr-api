using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Modules.Admin.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Tests.Admin;

/// <summary>
/// Super-Admin delete while the JWT still carries a different tenant id.
/// Child deletes must use the target id, not the ambient global query filter.
/// Catalog orders and product requests reference the customer and must not
/// block that delete. Global notification templates and unlinked trial claims stay.
/// </summary>
public sealed class AdminTenantDeleteAmbientFilterTests : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture _postgres;

    public AdminTenantDeleteAmbientFilterTests(PostgresContainerFixture postgres)
    {
        _postgres = postgres;
    }

    [DockerFact]
    public async Task Delete_while_entered_in_another_tenant_removes_target_children_and_leaves_the_other_tenants()
    {
        await using var scope = await SeedAsync(RequireFactory());
        var before = await scope.SnapshotAsync();
        scope.TenantProvider.TenantId = scope.EnteredId;

        await scope.Service.DeleteAsync(scope.TargetId, CancellationToken.None);

        var after = await scope.SnapshotAsync();
        AssertTargetRemoved(before, after, scope);
        AssertTenantUntouched(before, after, scope.EnteredId);
        AssertTenantUntouched(before, after, scope.KeeperId);
        AssertSharedRowsUntouched(before, after);
        Assert.Equal([scope.TargetOnlyAuthId], scope.Auth.DeletedIds);
    }

    [DockerFact]
    public async Task Delete_without_an_ambient_tenant_removes_target_children_and_leaves_the_other_tenants()
    {
        await using var scope = await SeedAsync(RequireFactory());
        var before = await scope.SnapshotAsync();
        scope.TenantProvider.TenantId = null;

        await scope.Service.DeleteAsync(scope.TargetId, CancellationToken.None);

        var after = await scope.SnapshotAsync();
        AssertTargetRemoved(before, after, scope);
        AssertTenantUntouched(before, after, scope.EnteredId);
        AssertTenantUntouched(before, after, scope.KeeperId);
        AssertSharedRowsUntouched(before, after);
        Assert.Equal([scope.TargetOnlyAuthId], scope.Auth.DeletedIds);
    }

    [DockerFact]
    public async Task Delete_while_entered_in_the_target_tenant_still_removes_only_that_tenant()
    {
        await using var scope = await SeedAsync(RequireFactory());
        var before = await scope.SnapshotAsync();
        scope.TenantProvider.TenantId = scope.TargetId;

        await scope.Service.DeleteAsync(scope.TargetId, CancellationToken.None);

        var after = await scope.SnapshotAsync();
        AssertTargetRemoved(before, after, scope);
        AssertTenantUntouched(before, after, scope.EnteredId);
        AssertTenantUntouched(before, after, scope.KeeperId);
        AssertSharedRowsUntouched(before, after);
        Assert.Equal([scope.TargetOnlyAuthId], scope.Auth.DeletedIds);
    }

    private PostgresAppDbFactory RequireFactory()
    {
        Assert.NotNull(_postgres.Factory);
        return _postgres.Factory;
    }

    private static void AssertTargetRemoved(Snapshot before, Snapshot after, Scope scope)
    {
        var targetBefore = before.ByTenant[scope.TargetId];
        Assert.True(targetBefore.TenantExists);
        Assert.Equal(2, targetBefore.Users);
        Assert.Equal(1, targetBefore.Units);
        Assert.Equal(1, targetBefore.Roles);
        Assert.Equal(1, targetBefore.Customers);
        Assert.Equal(1, targetBefore.Modules);
        Assert.Equal(1, targetBefore.OccupancyKinds);
        Assert.Equal(1, targetBefore.AssetCategories);
        Assert.Equal(1, targetBefore.CatalogOrders);
        Assert.Equal(1, targetBefore.CatalogOrderItems);
        Assert.Equal(1, targetBefore.CatalogOrderHistories);
        Assert.Equal(1, targetBefore.CatalogProducts);
        Assert.Equal(1, targetBefore.CatalogProductFiles);
        Assert.Equal(1, targetBefore.CatalogOrderSequences);
        Assert.Equal(1, targetBefore.ProductRequests);
        Assert.Equal(1, targetBefore.ProductRequestFiles);
        Assert.Equal(1, targetBefore.Notifications);
        Assert.Equal(1, targetBefore.NotificationDeliveries);
        Assert.Equal(1, targetBefore.NotificationDeliveryAttempts);
        Assert.Equal(1, targetBefore.ChannelConfigs);
        Assert.Equal(1, targetBefore.TrialSignupClaims);

        var targetAfter = after.ByTenant[scope.TargetId];
        Assert.False(targetAfter.TenantExists);
        Assert.Equal(0, targetAfter.Users);
        Assert.Equal(0, targetAfter.Units);
        Assert.Equal(0, targetAfter.Roles);
        Assert.Equal(0, targetAfter.Customers);
        Assert.Equal(0, targetAfter.Modules);
        Assert.Equal(0, targetAfter.OccupancyKinds);
        Assert.Equal(0, targetAfter.AssetCategories);
        Assert.Equal(0, targetAfter.CatalogOrders);
        Assert.Equal(0, targetAfter.CatalogOrderItems);
        Assert.Equal(0, targetAfter.CatalogOrderHistories);
        Assert.Equal(0, targetAfter.CatalogProducts);
        Assert.Equal(0, targetAfter.CatalogProductFiles);
        Assert.Equal(0, targetAfter.CatalogOrderSequences);
        Assert.Equal(0, targetAfter.ProductRequests);
        Assert.Equal(0, targetAfter.ProductRequestFiles);
        Assert.Equal(0, targetAfter.Notifications);
        Assert.Equal(0, targetAfter.NotificationDeliveries);
        Assert.Equal(0, targetAfter.NotificationDeliveryAttempts);
        Assert.Equal(0, targetAfter.ChannelConfigs);
        Assert.Equal(0, targetAfter.TrialSignupClaims);
    }

    private static void AssertSharedRowsUntouched(Snapshot before, Snapshot after)
    {
        Assert.Equal(before.NotificationTemplates, after.NotificationTemplates);
        Assert.True(after.NotificationTemplates >= 1);
        Assert.Equal(before.UnlinkedTrialClaims, after.UnlinkedTrialClaims);
        Assert.True(after.UnlinkedTrialClaims >= 1);
    }

    private static void AssertTenantUntouched(Snapshot before, Snapshot after, Guid tenantId)
    {
        Assert.Equal(before.ByTenant[tenantId], after.ByTenant[tenantId]);
        Assert.True(after.ByTenant[tenantId].TenantExists);
        Assert.True(after.ByTenant[tenantId].Users >= 1);
    }

    private static async Task<Scope> SeedAsync(PostgresAppDbFactory factory)
    {
        var tenantProvider = new FakeTenantProvider { TenantId = null };
        var db = factory.Create(tenantProvider);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var targetOnlyAuthId = Guid.NewGuid().ToString("N");
        var sharedAuthId = Guid.NewGuid().ToString("N");

        var entered = AddTenant(db, $"entered-{suffix}", "Entered");
        var target = AddTenant(db, $"target-{suffix}", "Target");
        var keeper = AddTenant(db, $"keeper-{suffix}", "Keeper");

        AddChildren(db, entered, Guid.NewGuid().ToString("N"), $"entered-{suffix}@club.test", "55111");
        AddChildren(db, target, targetOnlyAuthId, $"target-only-{suffix}@club.test", "55112");
        db.Users.Add(new User(
            target.Id,
            sharedAuthId,
            "Shared member",
            $"target-shared-{suffix}@club.test"));
        AddChildren(db, keeper, sharedAuthId, $"keeper-{suffix}@club.test", "55113");
        db.TrialSignupClaims.Add(new TrialSignupClaim(
            $"unlinked-{suffix}@trial.test",
            $"55000{suffix}"));
        db.NotificationTemplates.Add(new NotificationTemplate
        {
            EventType = $"catalog.order.created.{suffix}",
            Channel = NotificationChannel.Email,
            Language = "pt-BR",
            BodyTemplate = "Pedido {{orderNumber}}",
            SubjectTemplate = "Pedido",
        });

        await db.SaveChangesAsync();

        var auth = new RecordingAuthClient();
        var service = new AdminTenantService(
            db,
            new FakeTenantUserAdminService(),
            new FakePlatformAdminMembershipService(),
            new FakeTenantAccessBootstrapper(),
            auth,
            Options.Create(new PlatformAdminOptions()),
            NullLogger<AdminTenantService>.Instance);

        return new Scope(factory, db, tenantProvider, service, auth, entered.Id, target.Id, keeper.Id, targetOnlyAuthId);
    }

    private static Tenant AddTenant(AppDbContext db, string slug, string legalName)
    {
        var tenant = new Tenant(legalName, UniqueTaxId(), subdomain: slug);
        db.Tenants.Add(tenant);
        return tenant;
    }

    private static void AddChildren(
        AppDbContext db,
        Tenant tenant,
        string authId,
        string email,
        string phonePrefix)
    {
        db.Users.Add(new User(tenant.Id, authId, "Member", email));
        db.Units.Add(new Unit(tenant.Id, "Matriz", "matriz"));
        db.Roles.Add(new Role(tenant.Id, "ADMIN", isSystemRole: true));
        var customer = new Customer
        {
            TenantId = tenant.Id,
            Name = "Cliente",
            Email = email,
        };
        db.Customers.Add(customer);
        db.TenantModules.Add(new TenantModule(tenant.Id, PlatformModules.Rentals));
        db.OccupancyKinds.Add(new OccupancyKind
        {
            TenantId = tenant.Id,
            Key = "open",
            Label = "Aberto",
            IsBookableByCustomer = true,
            BlocksCapacity = true,
            SortOrder = 1,
            IsActive = true,
        });
        db.AssetCategories.Add(new AssetCategory
        {
            TenantId = tenant.Id,
            Name = "Quadras",
        });
        AddCatalogAndNotificationRows(db, tenant, customer, phonePrefix);
    }

    private static void AddCatalogAndNotificationRows(
        AppDbContext db,
        Tenant tenant,
        Customer customer,
        string phonePrefix)
    {
        var suffix = tenant.Subdomain;
        var product = new CatalogProduct
        {
            TenantId = tenant.Id,
            Name = "Item",
            Code = "ITEM",
            Price = 10m,
            Currency = "BRL",
            IsActive = true,
        };
        product.AddFile(new CatalogProductFile
        {
            TenantId = tenant.Id,
            ProductId = product.Id,
            StorageKey = $"{tenant.Id}/{product.Id}/photo.png",
            FileName = "photo.png",
            MimeType = "image/png",
            SizeBytes = 4,
            Visibility = CatalogFileVisibility.CustomerVisible,
        });
        db.CatalogProducts.Add(product);

        db.CatalogOrderNumberSequences.Add(new CatalogOrderNumberSequence
        {
            TenantId = tenant.Id,
            LastNumber = 1,
        });

        var order = new CatalogOrder
        {
            TenantId = tenant.Id,
            CustomerId = customer.Id,
            OrderNumber = 1,
            CustomerNameSnapshot = customer.Name,
            CustomerEmailSnapshot = customer.Email,
            Currency = "BRL",
        };
        order.AddItem(new CatalogOrderItem
        {
            TenantId = tenant.Id,
            OrderId = order.Id,
            ProductId = product.Id,
            ProductNameSnapshot = product.Name,
            Quantity = 1,
            UnitPriceSnapshot = 10m,
            SubTotal = 10m,
            Currency = "BRL",
        });
        order.RecordHistory(
            CatalogOrderStatus.Requested,
            CatalogActorType.Customer,
            customer.Id,
            reason: null);
        db.CatalogOrders.Add(order);

        var request = new ProductRequest
        {
            TenantId = tenant.Id,
            CustomerId = customer.Id,
            Description = "Item fora do catalogo",
            Quantity = 1,
            Status = ProductRequestStatus.Submitted,
        };
        request.AddFile(new ProductRequestFile
        {
            TenantId = tenant.Id,
            ProductRequestId = request.Id,
            StorageKey = $"{tenant.Id}/requests/{request.Id}/note.txt",
            FileName = "note.txt",
            MimeType = "text/plain",
            SizeBytes = 4,
            Visibility = CatalogFileVisibility.InternalB2B,
        });
        db.ProductRequests.Add(request);

        var notification = new Notification
        {
            TenantId = tenant.Id,
            EventType = "catalog.order.created",
            AggregateType = "CatalogOrder",
            AggregateId = order.Id,
            Payload = new Dictionary<string, string?> { ["orderNumber"] = "1" },
        };
        var delivery = new NotificationDelivery
        {
            TenantId = tenant.Id,
            NotificationId = notification.Id,
            Channel = NotificationChannel.Email,
            RecipientKind = NotificationRecipientKind.Customer,
            RecipientId = customer.Id,
            RecipientEmail = customer.Email,
            Status = NotificationDeliveryStatus.Sent,
        };
        delivery.AddAttempt(new NotificationDeliveryAttempt
        {
            DeliveryId = delivery.Id,
            AttemptNumber = 1,
            StartedAt = DateTimeOffset.UtcNow,
            Outcome = NotificationAttemptOutcome.Success,
        });
        notification.AddDelivery(delivery);
        db.Notifications.Add(notification);

        db.TenantNotificationChannelConfigs.Add(new TenantNotificationChannelConfig
        {
            TenantId = tenant.Id,
            EventType = "catalog.order.created",
            Channel = NotificationChannel.Email,
            IsActive = false,
        });

        var claim = new TrialSignupClaim($"claim-{suffix}@trial.test", $"{phonePrefix}{suffix}");
        claim.LinkTenant(tenant.Id);
        db.TrialSignupClaims.Add(claim);
    }

    private static string UniqueTaxId() => Guid.NewGuid().ToString("N")[..14];

    private sealed class Scope(
        PostgresAppDbFactory factory,
        AppDbContext db,
        FakeTenantProvider tenantProvider,
        AdminTenantService service,
        RecordingAuthClient auth,
        Guid enteredId,
        Guid targetId,
        Guid keeperId,
        string targetOnlyAuthId) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = db;

        public FakeTenantProvider TenantProvider { get; } = tenantProvider;

        public AdminTenantService Service { get; } = service;

        public RecordingAuthClient Auth { get; } = auth;

        public Guid EnteredId { get; } = enteredId;

        public Guid TargetId { get; } = targetId;

        public Guid KeeperId { get; } = keeperId;

        public string TargetOnlyAuthId { get; } = targetOnlyAuthId;

        public async Task<Snapshot> SnapshotAsync()
        {
            await using var verify = factory.Create(new FakeTenantProvider { TenantId = null });
            var ids = new[] { EnteredId, TargetId, KeeperId };
            var counts = new Dictionary<Guid, ChildCounts>();
            foreach (var id in ids)
            {
                var deliveryIds = verify.NotificationDeliveries
                    .IgnoreQueryFilters()
                    .Where(delivery => delivery.TenantId == id)
                    .Select(delivery => delivery.Id);

                counts[id] = new ChildCounts(
                    await verify.Tenants.AnyAsync(tenant => tenant.Id == id),
                    await verify.Users.IgnoreQueryFilters().CountAsync(row => row.TenantId == id),
                    await verify.Units.IgnoreQueryFilters().CountAsync(row => row.TenantId == id),
                    await verify.Roles.IgnoreQueryFilters().CountAsync(row => row.TenantId == id),
                    await verify.Customers.IgnoreQueryFilters().CountAsync(row => row.TenantId == id),
                    await verify.TenantModules.IgnoreQueryFilters().CountAsync(row => row.TenantId == id),
                    await verify.OccupancyKinds.IgnoreQueryFilters().CountAsync(row => row.TenantId == id),
                    await verify.AssetCategories.IgnoreQueryFilters().CountAsync(row => row.TenantId == id),
                    await verify.CatalogOrders.IgnoreQueryFilters().CountAsync(row => row.TenantId == id),
                    await verify.CatalogOrderItems.IgnoreQueryFilters().CountAsync(row => row.TenantId == id),
                    await verify.CatalogOrderStatusHistories.IgnoreQueryFilters().CountAsync(row => row.TenantId == id),
                    await verify.CatalogProducts.IgnoreQueryFilters().CountAsync(row => row.TenantId == id),
                    await verify.CatalogProductFiles.IgnoreQueryFilters().CountAsync(row => row.TenantId == id),
                    await verify.CatalogOrderNumberSequences.IgnoreQueryFilters().CountAsync(row => row.TenantId == id),
                    await verify.ProductRequests.IgnoreQueryFilters().CountAsync(row => row.TenantId == id),
                    await verify.ProductRequestFiles.IgnoreQueryFilters().CountAsync(row => row.TenantId == id),
                    await verify.Notifications.IgnoreQueryFilters().CountAsync(row => row.TenantId == id),
                    await verify.NotificationDeliveries.IgnoreQueryFilters().CountAsync(row => row.TenantId == id),
                    await verify.NotificationDeliveryAttempts.IgnoreQueryFilters().CountAsync(attempt => deliveryIds.Contains(attempt.DeliveryId)),
                    await verify.TenantNotificationChannelConfigs.IgnoreQueryFilters().CountAsync(row => row.TenantId == id),
                    await verify.TrialSignupClaims.IgnoreQueryFilters().CountAsync(row => row.TenantId == id));
            }

            return new Snapshot(
                counts,
                await verify.NotificationTemplates.CountAsync(),
                await verify.TrialSignupClaims.CountAsync(claim => claim.TenantId == null));
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed record Snapshot(
        IReadOnlyDictionary<Guid, ChildCounts> ByTenant,
        int NotificationTemplates,
        int UnlinkedTrialClaims);

    private sealed record ChildCounts(
        bool TenantExists,
        int Users,
        int Units,
        int Roles,
        int Customers,
        int Modules,
        int OccupancyKinds,
        int AssetCategories,
        int CatalogOrders,
        int CatalogOrderItems,
        int CatalogOrderHistories,
        int CatalogProducts,
        int CatalogProductFiles,
        int CatalogOrderSequences,
        int ProductRequests,
        int ProductRequestFiles,
        int Notifications,
        int NotificationDeliveries,
        int NotificationDeliveryAttempts,
        int ChannelConfigs,
        int TrialSignupClaims);

    private sealed class RecordingAuthClient : ISupabaseAuthAdminClient
    {
        public List<string> DeletedIds { get; } = [];

        public Task<string> CreateUserAsync(
            string email,
            string password,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("unused");

        public Task<string?> FindUserIdByEmailAsync(
            string email,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<bool> UserExistsAsync(
            string supabaseUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task UpdateUserAppMetadataAsync(
            string supabaseUserId,
            Guid tenantId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ClearUserTenantAppMetadataAsync(
            string supabaseUserId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<Guid?> GetUserTenantAppMetadataAsync(
            string supabaseUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Guid?>(null);

        public Task SetUserPasswordAsync(
            string supabaseUserId,
            string password,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteUserAsync(
            string supabaseUserId,
            CancellationToken cancellationToken = default)
        {
            DeletedIds.Add(supabaseUserId);
            return Task.CompletedTask;
        }

        public Task<SupabaseRecoveryLink> GenerateRecoveryLinkAsync(
            string email,
            string redirectTo,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SupabaseRecoveryLink("token", null));
    }
}
