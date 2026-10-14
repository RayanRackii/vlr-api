using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Modules.Admin.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Tests.Admin;

/// <summary>
/// Super-Admin delete while the JWT still carries a different tenant id.
/// Child deletes must use the target id, not the ambient global query filter.
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

        var targetAfter = after.ByTenant[scope.TargetId];
        Assert.False(targetAfter.TenantExists);
        Assert.Equal(0, targetAfter.Users);
        Assert.Equal(0, targetAfter.Units);
        Assert.Equal(0, targetAfter.Roles);
        Assert.Equal(0, targetAfter.Customers);
        Assert.Equal(0, targetAfter.Modules);
        Assert.Equal(0, targetAfter.OccupancyKinds);
        Assert.Equal(0, targetAfter.AssetCategories);
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

        AddChildren(db, entered, Guid.NewGuid().ToString("N"), $"entered-{suffix}@club.test");
        AddChildren(db, target, targetOnlyAuthId, $"target-only-{suffix}@club.test");
        db.Users.Add(new User(
            target.Id,
            sharedAuthId,
            "Shared member",
            $"target-shared-{suffix}@club.test"));
        AddChildren(db, keeper, sharedAuthId, $"keeper-{suffix}@club.test");

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

    private static void AddChildren(AppDbContext db, Tenant tenant, string authId, string email)
    {
        db.Users.Add(new User(tenant.Id, authId, "Member", email));
        db.Units.Add(new Unit(tenant.Id, "Matriz", "matriz"));
        db.Roles.Add(new Role(tenant.Id, "ADMIN", isSystemRole: true));
        db.Customers.Add(new Customer
        {
            TenantId = tenant.Id,
            Name = "Cliente",
            Email = email,
        });
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
                counts[id] = new ChildCounts(
                    await verify.Tenants.AnyAsync(tenant => tenant.Id == id),
                    await verify.Users.IgnoreQueryFilters().CountAsync(row => row.TenantId == id),
                    await verify.Units.IgnoreQueryFilters().CountAsync(row => row.TenantId == id),
                    await verify.Roles.IgnoreQueryFilters().CountAsync(row => row.TenantId == id),
                    await verify.Customers.IgnoreQueryFilters().CountAsync(row => row.TenantId == id),
                    await verify.TenantModules.IgnoreQueryFilters().CountAsync(row => row.TenantId == id),
                    await verify.OccupancyKinds.IgnoreQueryFilters().CountAsync(row => row.TenantId == id),
                    await verify.AssetCategories.IgnoreQueryFilters().CountAsync(row => row.TenantId == id));
            }

            return new Snapshot(counts);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed record Snapshot(IReadOnlyDictionary<Guid, ChildCounts> ByTenant);

    private sealed record ChildCounts(
        bool TenantExists,
        int Users,
        int Units,
        int Roles,
        int Customers,
        int Modules,
        int OccupancyKinds,
        int AssetCategories);

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
