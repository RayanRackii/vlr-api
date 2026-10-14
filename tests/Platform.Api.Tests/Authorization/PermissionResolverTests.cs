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
    public async Task Admin_wildcard_keys_omit_portal_customer_read()
    {
        await using var harness = await RbacResolverHarness.CreateAsync();

        var keys = await harness.Resolver.GetAdminWildcardKeysAsync(harness.Tenant.Id);

        Assert.Contains(Permissions.Core.UsersRead, keys);
        Assert.DoesNotContain(Permissions.Core.CustomersRead, keys);
    }

    [Fact]
    public async Task Custom_role_can_receive_portal_customer_read_explicitly()
    {
        await using var harness = await RbacResolverHarness.CreateAsync();
        var role = harness.AddCustomRole("PortalReaders", Permissions.Core.CustomersRead);
        var user = harness.AddUser("portal-reader");
        harness.Assign(user, role);
        await harness.Db.SaveChangesAsync();

        var effective = await harness.Resolver.GetEffectivePermissionsAsync(
            harness.Tenant.Id,
            user.Id);

        Assert.Contains(Permissions.Core.CustomersRead, effective);
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
        Assert.DoesNotContain(Permissions.Core.CustomersRead, effective);
        Assert.DoesNotContain(Permissions.Rentals.ReservationsRead, effective);
        Assert.DoesNotContain(Permissions.Catalog.OrdersRead, effective);
    }

    [Fact]
    public async Task Admin_wildcard_includes_catalog_when_module_enabled()
    {
        await using var harness = await RbacResolverHarness.CreateAsync();
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.Catalog, isActive: true));
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.Rentals, isActive: false));
        var adminRole = harness.AddSystemRole(SystemRoles.Admin);
        var admin = harness.AddUser("admin-catalog");
        harness.Assign(admin, adminRole);
        await harness.Db.SaveChangesAsync();

        var effective = await harness.Resolver.GetEffectivePermissionsAsync(
            harness.Tenant.Id,
            admin.Id);

        Assert.Contains(Permissions.Catalog.ProductsRead, effective);
        Assert.Contains(Permissions.Catalog.OrdersManage, effective);
        Assert.DoesNotContain(Permissions.Rentals.ReservationsRead, effective);
    }

    [Fact]
    public async Task Catalog_orders_manage_is_filtered_when_catalog_module_is_off()
    {
        await using var harness = await RbacResolverHarness.CreateAsync();
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.Catalog, isActive: false));
        var role = harness.AddCustomRole(
            "Ops",
            Permissions.Core.DashboardRead,
            Permissions.Catalog.OrdersManage,
            Permissions.Catalog.ProductsManage);
        var user = harness.AddUser("catalog-ops");
        harness.Assign(user, role);
        await harness.Db.SaveChangesAsync();

        var effective = await harness.Resolver.GetEffectivePermissionsAsync(
            harness.Tenant.Id,
            user.Id);

        Assert.Contains(Permissions.Core.DashboardRead, effective);
        Assert.DoesNotContain(Permissions.Catalog.OrdersManage, effective);
        Assert.DoesNotContain(Permissions.Catalog.ProductsManage, effective);
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

    [Fact]
    public async Task Inventory_off_rentals_on_includes_rentals_assets_write_not_inventory_assets_write()
    {
        await using var harness = await RbacResolverHarness.CreateAsync();
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.Inventory, isActive: false));
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.Rentals, isActive: true));
        var role = harness.AddCustomRole(
            "RentalOps",
            Permissions.Rentals.AssetsWrite,
            Permissions.Inventory.AssetsWrite);
        var user = harness.AddUser("rental-ops");
        harness.Assign(user, role);
        await harness.Db.SaveChangesAsync();

        var effective = await harness.Resolver.GetEffectivePermissionsAsync(
            harness.Tenant.Id,
            user.Id);

        Assert.Contains(Permissions.Rentals.AssetsWrite, effective);
        Assert.DoesNotContain(Permissions.Inventory.AssetsWrite, effective);
        Assert.DoesNotContain(Permissions.Inventory.AssetsRead, effective);
    }

    [Fact]
    public async Task Inventory_off_pmoc_on_includes_plans_read_not_inventory_categories_read()
    {
        await using var harness = await RbacResolverHarness.CreateAsync();
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.Inventory, isActive: false));
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.Pmoc, isActive: true));
        var role = harness.AddCustomRole(
            "PmocOps",
            Permissions.Pmoc.PlansRead,
            Permissions.Inventory.CategoriesRead);
        var user = harness.AddUser("pmoc-ops");
        harness.Assign(user, role);
        await harness.Db.SaveChangesAsync();

        var effective = await harness.Resolver.GetEffectivePermissionsAsync(
            harness.Tenant.Id,
            user.Id);

        Assert.Contains(Permissions.Pmoc.PlansRead, effective);
        Assert.DoesNotContain(Permissions.Inventory.CategoriesRead, effective);
    }

    [Fact]
    public async Task Inventory_off_os_on_includes_work_orders_read_not_inventory_assets_read()
    {
        await using var harness = await RbacResolverHarness.CreateAsync();
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.Inventory, isActive: false));
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.WorkOrders, isActive: true));
        var role = harness.AddCustomRole(
            "OsOps",
            Permissions.Os.WorkOrdersRead,
            Permissions.Inventory.AssetsRead);
        var user = harness.AddUser("os-ops");
        harness.Assign(user, role);
        await harness.Db.SaveChangesAsync();

        var effective = await harness.Resolver.GetEffectivePermissionsAsync(
            harness.Tenant.Id,
            user.Id);

        Assert.Contains(Permissions.Os.WorkOrdersRead, effective);
        Assert.DoesNotContain(Permissions.Inventory.AssetsRead, effective);
    }

    [Fact]
    public async Task Admin_wildcard_includes_core_notifications_when_only_rentals_is_active()
    {
        await using var harness = await RbacResolverHarness.CreateAsync();
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.Rentals, isActive: true));
        var adminRole = harness.AddSystemRole(SystemRoles.Admin);
        var admin = harness.AddUser("admin-rentals-notif");
        harness.Assign(admin, adminRole);
        await harness.Db.SaveChangesAsync();

        var effective = await harness.Resolver.GetEffectivePermissionsAsync(
            harness.Tenant.Id,
            admin.Id);

        Assert.Contains(Permissions.Core.NotificationsRead, effective);
        Assert.Contains(Permissions.Core.NotificationsWrite, effective);
        Assert.Contains(Permissions.Rentals.ReservationsRead, effective);
        Assert.Contains(Permissions.Rentals.ScheduleLessonsWrite, effective);
        Assert.DoesNotContain(Permissions.Catalog.OrdersRead, effective);
    }

    [Fact]
    public async Task Catalog_only_effective_set_has_no_inventory_permissions()
    {
        await using var harness = await RbacResolverHarness.CreateAsync();
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.Catalog, isActive: true));
        var adminRole = harness.AddSystemRole(SystemRoles.Admin);
        var admin = harness.AddUser("catalog-only-admin");
        harness.Assign(admin, adminRole);
        await harness.Db.SaveChangesAsync();

        var effective = await harness.Resolver.GetEffectivePermissionsAsync(
            harness.Tenant.Id,
            admin.Id);

        Assert.Contains(Permissions.Catalog.ProductsRead, effective);
        Assert.Contains(Permissions.Catalog.OrdersManage, effective);
        Assert.DoesNotContain(Permissions.Inventory.AssetsRead, effective);
        Assert.DoesNotContain(Permissions.Inventory.AssetsWrite, effective);
        Assert.DoesNotContain(Permissions.Inventory.CategoriesRead, effective);
        Assert.DoesNotContain(Permissions.Inventory.CategoriesWrite, effective);
        Assert.DoesNotContain(Permissions.Inventory.FamiliesRead, effective);
        Assert.DoesNotContain(effective, key => key.StartsWith("inventory.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mismatched_ambient_returns_empty_for_other_tenant_admin_even_when_modules_would_grant()
    {
        await using var harness = await RbacResolverHarness.CreateAsync();
        var tenantB = AddTenant(harness, "Other RBAC", "22222222000191");
        harness.Db.TenantModules.Add(new TenantModule(tenantB.Id, PlatformModules.WorkOrders, isActive: true));
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.Catalog, isActive: true));
        var admin = AddAdmin(harness, tenantB, "admin-b");
        await harness.Db.SaveChangesAsync();

        var effective = await harness.Resolver.GetEffectivePermissionsAsync(tenantB.Id, admin.Id);

        Assert.Empty(effective);
        Assert.DoesNotContain(Permissions.Os.WorkOrdersRead, effective);
        Assert.DoesNotContain(Permissions.Core.UsersRead, effective);
        Assert.DoesNotContain(Permissions.Catalog.OrdersManage, effective);
    }

    [Fact]
    public async Task Mismatched_ambient_admin_wildcard_is_empty_even_when_ambient_has_os()
    {
        await using var harness = await RbacResolverHarness.CreateAsync();
        var tenantB = AddTenant(harness, "Wildcard B", "33333333000191");
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.WorkOrders, isActive: true));
        harness.Db.TenantModules.Add(new TenantModule(tenantB.Id, PlatformModules.WorkOrders, isActive: true));
        await harness.Db.SaveChangesAsync();

        var keys = await harness.Resolver.GetAdminWildcardKeysAsync(tenantB.Id);

        Assert.Empty(keys);
        Assert.DoesNotContain(keys, key => key.StartsWith("os.", StringComparison.Ordinal));
        Assert.DoesNotContain(Permissions.Core.UsersRead, keys);
    }

    [Fact]
    public async Task Null_ambient_returns_empty_for_real_admin_including_core_keys()
    {
        await using var harness = await RbacResolverHarness.CreateAsync();
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.WorkOrders, isActive: true));
        var adminRole = harness.AddSystemRole(SystemRoles.Admin);
        var admin = harness.AddUser("null-ambient-admin");
        harness.Assign(admin, adminRole);
        await harness.Db.SaveChangesAsync();
        harness.TenantProvider.TenantId = null;

        var effective = await harness.Resolver.GetEffectivePermissionsAsync(harness.Tenant.Id, admin.Id);
        var wildcard = await harness.Resolver.GetAdminWildcardKeysAsync(harness.Tenant.Id);

        Assert.Empty(effective);
        Assert.Empty(wildcard);
        Assert.DoesNotContain(Permissions.Core.UsersRead, effective);
        Assert.DoesNotContain(Permissions.Core.DashboardRead, effective);
        Assert.DoesNotContain(Permissions.Core.UsersRead, wildcard);
        Assert.DoesNotContain(Permissions.Os.WorkOrdersRead, wildcard);
    }

    [Fact]
    public async Task Matching_ambient_still_returns_enabled_module_keys()
    {
        await using var harness = await RbacResolverHarness.CreateAsync();
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.WorkOrders, isActive: true));
        var adminRole = harness.AddSystemRole(SystemRoles.Admin);
        var admin = harness.AddUser("matching-admin");
        harness.Assign(admin, adminRole);
        await harness.Db.SaveChangesAsync();

        var effective = await harness.Resolver.GetEffectivePermissionsAsync(harness.Tenant.Id, admin.Id);
        var wildcard = await harness.Resolver.GetAdminWildcardKeysAsync(harness.Tenant.Id);

        Assert.Contains(Permissions.Os.WorkOrdersRead, effective);
        Assert.Contains(Permissions.Os.WorkOrdersExecute, effective);
        Assert.Contains(Permissions.Core.UsersRead, effective);
        Assert.Contains(Permissions.Os.WorkOrdersRead, wildcard);
        Assert.DoesNotContain(Permissions.Catalog.OrdersManage, effective);
    }

    [Fact]
    public async Task Ambient_mismatch_is_not_memoized()
    {
        await using var harness = await RbacResolverHarness.CreateAsync();
        var tenantB = AddTenant(harness, "Memo B", "44444444000191");
        harness.Db.TenantModules.Add(new TenantModule(tenantB.Id, PlatformModules.WorkOrders, isActive: true));
        var admin = AddAdmin(harness, tenantB, "memo-admin");
        await harness.Db.SaveChangesAsync();

        var missed = await harness.Resolver.GetEffectivePermissionsAsync(tenantB.Id, admin.Id);
        Assert.Empty(missed);

        harness.TenantProvider.TenantId = tenantB.Id;
        var matched = await harness.Resolver.GetEffectivePermissionsAsync(tenantB.Id, admin.Id);
        Assert.Contains(Permissions.Os.WorkOrdersExecute, matched);
        Assert.Contains(Permissions.Core.UsersRead, matched);

        harness.TenantProvider.TenantId = harness.Tenant.Id;
        var revoked = await harness.Resolver.GetEffectivePermissionsAsync(tenantB.Id, admin.Id);
        Assert.Empty(revoked);

        harness.TenantProvider.TenantId = null;
        Assert.Empty(await harness.Resolver.GetAdminWildcardKeysAsync(tenantB.Id));

        harness.TenantProvider.TenantId = tenantB.Id;
        var wildcard = await harness.Resolver.GetAdminWildcardKeysAsync(tenantB.Id);
        Assert.Contains(Permissions.Os.WorkOrdersRead, wildcard);
    }

    [Fact]
    public async Task Mismatched_ambient_does_not_load_modules()
    {
        await using var harness = await RbacResolverHarness.CreateAsync();
        var tenantB = AddTenant(harness, "No Load", "55555555000191");
        var admin = AddAdmin(harness, tenantB, "noload-admin");
        await harness.Db.SaveChangesAsync();

        var accessor = new CountingModuleAccessor(PlatformModules.WorkOrders);
        var resolver = new PermissionResolver(
            harness.Db,
            accessor,
            harness.TenantProvider,
            NullLogger<PermissionResolver>.Instance);

        Assert.Empty(await resolver.GetEffectivePermissionsAsync(tenantB.Id, admin.Id));
        Assert.Empty(await resolver.GetAdminWildcardKeysAsync(tenantB.Id));
        Assert.Equal(0, accessor.Calls);

        harness.TenantProvider.TenantId = tenantB.Id;
        var matched = await resolver.GetEffectivePermissionsAsync(tenantB.Id, admin.Id);
        Assert.Contains(Permissions.Os.WorkOrdersExecute, matched);
        Assert.Equal(1, accessor.Calls);
    }

    private static Tenant AddTenant(RbacResolverHarness harness, string name, string document)
    {
        var tenant = new Tenant(name, document);
        harness.Db.Tenants.Add(tenant);
        return tenant;
    }

    private static User AddAdmin(RbacResolverHarness harness, Tenant tenant, string authId)
    {
        var role = new Role(tenant.Id, SystemRoles.Admin, "Admin (system)", isSystemRole: true);
        harness.Db.Roles.Add(role);
        var user = new User(tenant.Id, authId, authId, $"{authId}@test.com");
        harness.Db.Users.Add(user);
        harness.Db.UserRoles.Add(new UserRole(user.Id, role.Id));
        return user;
    }

    private sealed class CountingModuleAccessor(params string[] keys) : ITenantModuleAccessor
    {
        public int Calls { get; private set; }

        public Task<IReadOnlySet<string>> GetActiveModuleKeysAsync(
            CancellationToken cancellationToken = default)
        {
            Calls++;
            IReadOnlySet<string> set = keys.ToHashSet(StringComparer.Ordinal);
            return Task.FromResult(set);
        }
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
        var resolver = TestPermissionResolvers.Create(db, tenantProvider);
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
