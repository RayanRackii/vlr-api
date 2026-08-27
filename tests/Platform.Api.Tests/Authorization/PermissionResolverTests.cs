using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Authorization;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Authorization;

public sealed class PermissionResolverTests
{
    [Fact]
    public async Task Union_of_role_permissions_is_effective_set()
    {
        await using var harness = await RbacResolverHarness.CreateAsync();
        var reader = harness.AddCustomRole("Reader", Permissions.Core.DashboardRead);
        var units = harness.AddCustomRole("Units", Permissions.Core.UnitsRead);
        var user = harness.AddUser("union-user");
        harness.Assign(user, reader, units);
        await harness.Db.SaveChangesAsync();

        var effective = await harness.Resolver.GetEffectivePermissionsAsync(
            harness.Tenant.Id,
            user.Id);

        Assert.Contains(Permissions.Core.DashboardRead, effective);
        Assert.Contains(Permissions.Core.UnitsRead, effective);
        Assert.DoesNotContain(Permissions.Core.UsersRead, effective);
    }

    [Fact]
    public async Task Admin_wildcard_includes_enabled_catalog_and_excludes_disabled_module()
    {
        await using var harness = await RbacResolverHarness.CreateAsync();
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.Rentals, isActive: false));
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.WorkOrders, isActive: true));
        var adminRole = harness.AddSystemRole(SystemRoles.Admin);
        var admin = harness.AddUser("admin-user");
        harness.Assign(admin, adminRole);
        await harness.Db.SaveChangesAsync();

        var effective = await harness.Resolver.GetEffectivePermissionsAsync(
            harness.Tenant.Id,
            admin.Id);

        Assert.Contains(Permissions.Core.DashboardRead, effective);
        Assert.Contains(Permissions.Os.WorkOrdersRead, effective);
        Assert.DoesNotContain(Permissions.Rentals.ReservationsRead, effective);
    }

    [Fact]
    public async Task Module_disabled_filters_union_permissions()
    {
        await using var harness = await RbacResolverHarness.CreateAsync();
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.Inventory, isActive: false));
        var role = harness.AddCustomRole(
            "TechLite",
            Permissions.Inventory.AssetsRead,
            Permissions.Os.WorkOrdersExecute);
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.WorkOrders, isActive: true));
        var user = harness.AddUser("module-user");
        harness.Assign(user, role);
        await harness.Db.SaveChangesAsync();

        var effective = await harness.Resolver.GetEffectivePermissionsAsync(
            harness.Tenant.Id,
            user.Id);

        Assert.Contains(Permissions.Os.WorkOrdersExecute, effective);
        Assert.DoesNotContain(Permissions.Inventory.AssetsRead, effective);
    }

    [Fact]
    public async Task Tenant_isolation_does_not_leak_other_tenant_roles()
    {
        await using var harness = await RbacResolverHarness.CreateAsync();
        var otherTenant = new Tenant("Other Club", "22222222000191");
        harness.Db.Tenants.Add(otherTenant);
        var leakedRole = new Role(otherTenant.Id, "Leaked", isSystemRole: false);
        harness.Db.Roles.Add(leakedRole);
        var perm = harness.Db.Permissions.First(p => p.Key == Permissions.Core.UsersRead);
        harness.Db.RolePermissions.Add(new RolePermission(leakedRole.Id, perm.Id));
        var otherUser = new User(otherTenant.Id, "other-auth", "Other", "other@test.com");
        harness.Db.Users.Add(otherUser);
        harness.Db.UserRoles.Add(new UserRole(otherUser.Id, leakedRole.Id));

        var localRole = harness.AddCustomRole("Local", Permissions.Core.DashboardRead);
        var localUser = harness.AddUser("local-user");
        harness.Assign(localUser, localRole);
        await harness.Db.SaveChangesAsync();

        var forLocal = await harness.Resolver.GetEffectivePermissionsAsync(
            harness.Tenant.Id,
            localUser.Id);
        var crossUser = await harness.Resolver.GetEffectivePermissionsAsync(
            harness.Tenant.Id,
            otherUser.Id);
        var crossTenant = await harness.Resolver.GetEffectivePermissionsAsync(
            otherTenant.Id,
            localUser.Id);

        Assert.Equal([Permissions.Core.DashboardRead], forLocal.OrderBy(k => k));
        Assert.Empty(crossUser);
        Assert.Empty(crossTenant);
    }

    [Fact]
    public async Task Fail_closed_for_inactive_missing_and_empty_ids()
    {
        await using var harness = await RbacResolverHarness.CreateAsync();
        var role = harness.AddCustomRole("Any", Permissions.Core.DashboardRead);
        var inactive = harness.AddUser("inactive-user");
        inactive.Deactivate();
        harness.Assign(inactive, role);
        await harness.Db.SaveChangesAsync();

        Assert.Empty(await harness.Resolver.GetEffectivePermissionsAsync(harness.Tenant.Id, inactive.Id));
        Assert.Empty(await harness.Resolver.GetEffectivePermissionsAsync(harness.Tenant.Id, Guid.NewGuid()));
        Assert.Empty(await harness.Resolver.GetEffectivePermissionsAsync(Guid.Empty, Guid.NewGuid()));
        Assert.False(
            await harness.Resolver.HasPermissionAsync(
                harness.Tenant.Id,
                inactive.Id,
                Permissions.Core.DashboardRead));
    }

    [Fact]
    public async Task SuperAdmin_wildcard_matches_Admin()
    {
        await using var harness = await RbacResolverHarness.CreateAsync();
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.Pmoc, isActive: true));
        var super = harness.AddSystemRole(SystemRoles.SuperAdmin);
        var user = harness.AddUser("super-user");
        harness.Assign(user, super);
        await harness.Db.SaveChangesAsync();

        var effective = await harness.Resolver.GetEffectivePermissionsAsync(
            harness.Tenant.Id,
            user.Id);

        Assert.Contains(Permissions.Core.RolesManage, effective);
        Assert.Contains(Permissions.Pmoc.PlansWrite, effective);
        Assert.DoesNotContain(Permissions.Rentals.LayoutsWrite, effective);
    }
}

internal sealed class RbacResolverHarness : IAsyncDisposable
{
    private RbacResolverHarness(
        AppDbContext db,
        FakeTenantProvider tenantProvider,
        Tenant tenant,
        PermissionResolver resolver)
    {
        Db = db;
        TenantProvider = tenantProvider;
        Tenant = tenant;
        Resolver = resolver;
    }

    public AppDbContext Db { get; }

    public FakeTenantProvider TenantProvider { get; }

    public Tenant Tenant { get; }

    public PermissionResolver Resolver { get; }

    public static async Task<RbacResolverHarness> CreateAsync()
    {
        var tenant = new Tenant("RBAC Club", "11111111000191");
        var tenantProvider = new FakeTenantProvider { TenantId = tenant.Id };
        var db = InMemoryAppDb.Create(tenantProvider);
        db.Tenants.Add(tenant);
        foreach (var entry in PermissionCatalog.All)
        {
            db.Permissions.Add(new Permission(entry.Key, entry.Name, entry.Description, entry.ModuleKey));
        }

        await db.SaveChangesAsync();
        var resolver = new PermissionResolver(db, NullLogger<PermissionResolver>.Instance);
        return new RbacResolverHarness(db, tenantProvider, tenant, resolver);
    }

    public Role AddCustomRole(string name, params string[] keys)
    {
        var role = new Role(Tenant.Id, name, isSystemRole: false);
        Db.Roles.Add(role);
        Grant(role, keys);
        return role;
    }

    public Role AddSystemRole(string name)
    {
        var role = new Role(Tenant.Id, name, $"{name} (system)", isSystemRole: true);
        Db.Roles.Add(role);
        return role;
    }

    public User AddUser(string supabaseAuthId)
    {
        var user = new User(Tenant.Id, supabaseAuthId, supabaseAuthId, $"{supabaseAuthId}@test.com");
        Db.Users.Add(user);
        return user;
    }

    public void Assign(User user, params Role[] roles)
    {
        foreach (var role in roles)
        {
            Db.UserRoles.Add(new UserRole(user.Id, role.Id));
        }
    }

    public void Grant(Role role, params string[] keys)
    {
        foreach (var key in keys)
        {
            var permission = Db.Permissions.Local.First(p => p.Key == key);
            Db.RolePermissions.Add(new RolePermission(role.Id, permission.Id));
        }
    }

    public async ValueTask DisposeAsync() => await Db.DisposeAsync();
}
