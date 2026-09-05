using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Authorization;
using Platform.Api.Modules.Roles.Dtos;
using Platform.Api.Modules.Roles.Services;
using Platform.Api.Modules.Users.Dtos;
using Platform.Api.Modules.Users.Services;
using Platform.Api.Notifications;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Rbac;

public sealed class TenantRbacAccessTests
{
    [Fact]
    public async Task EnsureAsync_inserts_missing_catalog_keys_so_custom_role_can_persist_them()
    {
        await using var harness = await RbacAccessHarness.CreateAsync(includeCatalogPermissionRows: false);
        var adminRole = harness.AddSystemRole(SystemRoles.Admin);
        var admin = harness.AddUser("tenant-admin");
        harness.Assign(admin, adminRole);
        await harness.Db.SaveChangesAsync();

        var actor = new RbacActor(harness.Tenant.Id, admin.Id, IsPlatformAdminInTenant: false);
        var dropped = await harness.Roles.CreateAsync(
            harness.Tenant.Id,
            actor,
            new CreateRoleRequest(
                "OpsBefore",
                null,
                [Permissions.Core.DashboardRead, Permissions.Catalog.OrdersManage]),
            CancellationToken.None);

        Assert.Contains(Permissions.Core.DashboardRead, dropped.PermissionKeys);
        Assert.DoesNotContain(Permissions.Catalog.OrdersManage, dropped.PermissionKeys);

        var bootstrapper = new TenantAccessBootstrapper(harness.Db);
        await bootstrapper.EnsureAsync(harness.Tenant.Id);

        var persisted = await harness.Roles.CreateAsync(
            harness.Tenant.Id,
            actor,
            new CreateRoleRequest(
                "OpsAfter",
                null,
                [Permissions.Core.DashboardRead, Permissions.Catalog.OrdersManage]),
            CancellationToken.None);

        Assert.Contains(Permissions.Catalog.OrdersManage, persisted.PermissionKeys);
    }

    [Fact]
    public async Task Non_admin_cannot_persist_inactive_module_keys_on_a_custom_role()
    {
        await using var harness = await RbacAccessHarness.CreateAsync();
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.Rentals, isActive: false));
        var managerRole = harness.AddCustomRole(
            "RoleEditor",
            Permissions.Core.RolesManage,
            Permissions.Core.RolesRead,
            Permissions.Core.DashboardRead);
        var editor = harness.AddUser("role-editor");
        harness.Assign(editor, managerRole);
        await harness.Db.SaveChangesAsync();

        var actor = new RbacActor(harness.Tenant.Id, editor.Id, IsPlatformAdminInTenant: false);

        var ex = await Assert.ThrowsAsync<RbacException>(
            () => harness.Roles.CreateAsync(
                harness.Tenant.Id,
                actor,
                new CreateRoleRequest(
                    "Sneaky",
                    null,
                    [Permissions.Core.DashboardRead, Permissions.Rentals.ScheduleWrite]),
                CancellationToken.None));

        Assert.Equal(RbacErrorCodes.PrivilegeEscalationBlocked, ex.Code);
    }

    [Fact]
    public async Task Tenant_admin_can_persist_inactive_module_keys_on_a_custom_role()
    {
        await using var harness = await RbacAccessHarness.CreateAsync();
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.Rentals, isActive: false));
        var adminRole = harness.AddSystemRole(SystemRoles.Admin);
        var admin = harness.AddUser("tenant-admin");
        harness.Assign(admin, adminRole);
        await harness.Db.SaveChangesAsync();

        var actor = new RbacActor(harness.Tenant.Id, admin.Id, IsPlatformAdminInTenant: false);
        var created = await harness.Roles.CreateAsync(
            harness.Tenant.Id,
            actor,
            new CreateRoleRequest(
                "FrontDesk",
                null,
                [Permissions.Core.DashboardRead, Permissions.Rentals.ScheduleWrite]),
            CancellationToken.None);

        Assert.Contains(Permissions.Rentals.ScheduleWrite, created.PermissionKeys);
    }

    [Fact]
    public async Task Tenant_admin_can_assign_seeded_user_role_when_a_user_bundle_module_is_inactive()
    {
        await using var harness = await RbacAccessHarness.CreateAsync();
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.Rentals, isActive: false));
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.WorkOrders, isActive: true));
        var adminRole = harness.AddSystemRole(SystemRoles.Admin);
        var userRole = harness.AddSystemRole(SystemRoles.User);
        var admin = harness.AddUser("tenant-admin");
        var member = harness.AddUser("member");
        harness.Assign(admin, adminRole);
        await harness.Db.SaveChangesAsync();

        var actor = new RbacActor(harness.Tenant.Id, admin.Id, IsPlatformAdminInTenant: false);

        await harness.Users.AssignRolesAsync(
            member.Id,
            actor,
            [userRole.Id],
            CancellationToken.None);

        var assigned = await harness.Db.UserRoles
            .Where(item => item.UserId == member.Id)
            .Select(item => item.RoleId)
            .ToListAsync();

        Assert.Equal([userRole.Id], assigned);
    }

    [Fact]
    public async Task Privilege_escalation_is_blocked_when_granting_stronger_role()
    {
        await using var harness = await RbacAccessHarness.CreateAsync();
        var actorRole = harness.AddCustomRole("Weak", Permissions.Core.DashboardRead);
        var strongRole = harness.AddCustomRole("Strong", Permissions.Core.UsersRead);
        var actor = harness.AddUser("actor");
        var target = harness.AddUser("target");
        harness.Assign(actor, actorRole);
        harness.Assign(target, actorRole);
        await harness.Db.SaveChangesAsync();

        var actorContext = new RbacActor(harness.Tenant.Id, actor.Id, IsPlatformAdminInTenant: false);

        var ex = await Assert.ThrowsAsync<RbacException>(
            () => harness.Users.AssignRolesAsync(
                target.Id,
                actorContext,
                [strongRole.Id],
                CancellationToken.None));

        Assert.Equal(RbacErrorCodes.PrivilegeEscalationBlocked, ex.Code);
        Assert.Equal(403, ex.HttpStatus);
    }

    [Fact]
    public async Task Last_admin_cannot_be_demoted()
    {
        await using var harness = await RbacAccessHarness.CreateAsync();
        var adminRole = harness.AddSystemRole(SystemRoles.Admin);
        var userRole = harness.AddSystemRole(SystemRoles.User);
        var admin = harness.AddUser("only-admin");
        harness.Assign(admin, adminRole);
        await harness.Db.SaveChangesAsync();

        var actor = new RbacActor(harness.Tenant.Id, admin.Id, IsPlatformAdminInTenant: true);

        var ex = await Assert.ThrowsAsync<RbacException>(
            () => harness.Users.AssignRolesAsync(
                admin.Id,
                actor,
                [userRole.Id],
                CancellationToken.None));

        Assert.Equal(RbacErrorCodes.LastAdminProtected, ex.Code);
        Assert.Equal(409, ex.HttpStatus);
    }

    [Fact]
    public async Task Custom_role_in_use_cannot_be_deleted()
    {
        await using var harness = await RbacAccessHarness.CreateAsync();
        var role = harness.AddCustomRole("InUse", Permissions.Core.DashboardRead);
        var user = harness.AddUser("member");
        harness.Assign(user, role);
        await harness.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<RbacException>(
            () => harness.Roles.DeleteAsync(harness.Tenant.Id, role.Id, CancellationToken.None));

        Assert.Equal(RbacErrorCodes.RoleInUse, ex.Code);
        Assert.Equal(409, ex.HttpStatus);
    }

    [Fact]
    public async Task Invite_with_role_ids_persists_user_invite_roles()
    {
        await using var harness = await RbacAccessHarness.CreateAsync();
        var roleA = harness.AddCustomRole("Desk", Permissions.Core.DashboardRead);
        var roleB = harness.AddCustomRole("Units", Permissions.Core.UnitsRead);
        var adminRole = harness.AddSystemRole(SystemRoles.Admin);
        var admin = harness.AddUser("inviter");
        harness.Assign(admin, adminRole);
        await harness.Db.SaveChangesAsync();

        var actor = new RbacActor(harness.Tenant.Id, admin.Id, IsPlatformAdminInTenant: true);
        var invite = await harness.Users.InviteAsync(
            actor,
            new InviteTenantMemberRequest("New Member", "new@test.com", [roleA.Id, roleB.Id]),
            CancellationToken.None);

        var stored = await harness.Db.UserInvites
            .Include(item => item.InviteRoles)
            .FirstAsync(item => item.Id == invite.Id);

        Assert.Equal(2, stored.InviteRoles.Count);
        Assert.Contains(stored.InviteRoles, item => item.RoleId == roleA.Id);
        Assert.Contains(stored.InviteRoles, item => item.RoleId == roleB.Id);
        Assert.Equal(roleA.Name, stored.RoleName);
    }

    [Fact]
    public async Task Accept_invite_uses_user_invite_roles_when_present()
    {
        await using var harness = await RbacAccessHarness.CreateAsync();
        var roleA = harness.AddCustomRole("Desk", Permissions.Core.DashboardRead);
        var roleB = harness.AddCustomRole("Units", Permissions.Core.UnitsRead);
        var invite = new UserInvite(
            harness.Tenant.Id,
            "join@test.com",
            "Join Me",
            SystemRoles.User,
            "accept-token-roleids",
            DateTimeOffset.UtcNow.AddDays(1));
        harness.Db.UserInvites.Add(invite);
        harness.Db.UserInviteRoles.Add(new UserInviteRole(invite.Id, roleA.Id));
        harness.Db.UserInviteRoles.Add(new UserInviteRole(invite.Id, roleB.Id));
        await harness.Db.SaveChangesAsync();

        var result = await harness.AdminInvites.AcceptInviteAsync(
            new Platform.Api.Modules.Admin.Dtos.AcceptInviteRequestDto
            {
                Token = "accept-token-roleids",
                Password = "password1",
            },
            CancellationToken.None);

        var user = await harness.Db.Users
            .Include(item => item.UserRoles)
            .FirstAsync(item => item.Id == result.UserId);

        Assert.Equal(2, user.UserRoles.Count);
        Assert.Contains(user.UserRoles, item => item.RoleId == roleA.Id);
        Assert.Contains(user.UserRoles, item => item.RoleId == roleB.Id);
    }

    [Fact]
    public async Task Accept_invite_falls_back_to_role_name_when_no_invite_roles()
    {
        await using var harness = await RbacAccessHarness.CreateAsync();
        var userRole = harness.AddSystemRole(SystemRoles.User);
        var invite = new UserInvite(
            harness.Tenant.Id,
            "legacy@test.com",
            "Legacy User",
            SystemRoles.User,
            "accept-token-legacy",
            DateTimeOffset.UtcNow.AddDays(1));
        harness.Db.UserInvites.Add(invite);
        await harness.Db.SaveChangesAsync();

        var result = await harness.AdminInvites.AcceptInviteAsync(
            new Platform.Api.Modules.Admin.Dtos.AcceptInviteRequestDto
            {
                Token = "accept-token-legacy",
                Password = "password1",
            },
            CancellationToken.None);

        var user = await harness.Db.Users
            .Include(item => item.UserRoles)
            .FirstAsync(item => item.Id == result.UserId);

        var assigned = Assert.Single(user.UserRoles);
        Assert.Equal(userRole.Id, assigned.RoleId);
    }

    [Fact]
    public async Task Technicians_list_uses_execute_permission_not_role_name()
    {
        await using var harness = await RbacAccessHarness.CreateAsync();
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.WorkOrders, isActive: true));
        var executeRole = harness.AddCustomRole(
            "Executor",
            Permissions.Os.WorkOrdersExecute,
            Permissions.Os.WorkOrdersRead);
        var namedTech = harness.AddSystemRole(SystemRoles.Technician);
        var executor = harness.AddUser("executor");
        var leftoverName = harness.AddUser("named-only");
        harness.Assign(executor, executeRole);
        harness.Assign(leftoverName, namedTech);
        await harness.Db.SaveChangesAsync();

        var listed = await harness.Users.ListTechniciansAsync(CancellationToken.None);

        Assert.Contains(listed, item => item.Id == executor.Id);
        Assert.DoesNotContain(listed, item => item.Id == leftoverName.Id);
    }

    [Fact]
    public async Task SuperAdmin_cannot_be_assigned_via_tenant_api()
    {
        await using var harness = await RbacAccessHarness.CreateAsync();
        var super = harness.AddSystemRole(SystemRoles.SuperAdmin);
        var adminRole = harness.AddSystemRole(SystemRoles.Admin);
        var admin = harness.AddUser("admin");
        var target = harness.AddUser("target");
        harness.Assign(admin, adminRole);
        harness.Assign(target, adminRole);
        var secondAdmin = harness.AddUser("admin-2");
        harness.Assign(secondAdmin, adminRole);
        await harness.Db.SaveChangesAsync();

        var actor = new RbacActor(harness.Tenant.Id, admin.Id, IsPlatformAdminInTenant: true);

        var ex = await Assert.ThrowsAsync<RbacException>(
            () => harness.Users.AssignRolesAsync(
                target.Id,
                actor,
                [super.Id],
                CancellationToken.None));

        Assert.Equal(RbacErrorCodes.CannotAssignSuperAdmin, ex.Code);
    }
}

internal sealed class RbacAccessHarness : IAsyncDisposable
{
    private RbacAccessHarness(
        AppDbContext db,
        FakeTenantProvider tenantProvider,
        Tenant tenant,
        UserDirectoryService users,
        RoleService roles,
        Platform.Api.Modules.Admin.Services.TenantUserAdminService adminInvites)
    {
        Db = db;
        TenantProvider = tenantProvider;
        Tenant = tenant;
        Users = users;
        Roles = roles;
        AdminInvites = adminInvites;
    }

    public AppDbContext Db { get; }

    public FakeTenantProvider TenantProvider { get; }

    public Tenant Tenant { get; }

    public UserDirectoryService Users { get; }

    public RoleService Roles { get; }

    public Platform.Api.Modules.Admin.Services.TenantUserAdminService AdminInvites { get; }

    public static async Task<RbacAccessHarness> CreateAsync(bool includeCatalogPermissionRows = true)
    {
        var tenant = new Tenant("Access Club", "44444444000191");
        var tenantProvider = new FakeTenantProvider { TenantId = tenant.Id };
        var db = InMemoryAppDb.Create(tenantProvider);
        db.Tenants.Add(tenant);
        foreach (var entry in PermissionCatalog.All)
        {
            if (!includeCatalogPermissionRows
                && entry.ModuleKey == PlatformModules.Catalog)
            {
                continue;
            }

            db.Permissions.Add(new Permission(entry.Key, entry.Name, entry.Description, entry.ModuleKey));
        }

        await db.SaveChangesAsync();

        var resolver = TestPermissionResolvers.Create(db, tenantProvider);
        var grantGuard = new RbacGrantGuard(db, resolver, NullLogger<RbacGrantGuard>.Instance);
        var configuration = new ConfigurationBuilder().Build();
        var users = new UserDirectoryService(
            db,
            tenantProvider,
            new FakePlatformAdminChecker(),
            resolver,
            grantGuard,
            new FakeTrialGuard(),
            new NotificationQueue(),
            configuration,
            new FakeHostEnvironment(),
            NullLogger<UserDirectoryService>.Instance);
        var roles = new RoleService(db, grantGuard, NullLogger<RoleService>.Instance);
        var adminInvites = new Platform.Api.Modules.Admin.Services.TenantUserAdminService(
            db,
            new FakeSupabaseAuthAdminClient(),
            new NotificationQueue(),
            configuration,
            new FakeHostEnvironment(),
            new FakeTrialGuard(),
            new FakePlatformAdminChecker(),
            NullLogger<Platform.Api.Modules.Admin.Services.TenantUserAdminService>.Instance);

        return new RbacAccessHarness(db, tenantProvider, tenant, users, roles, adminInvites);
    }

    public Role AddCustomRole(string name, params string[] keys)
    {
        var role = new Role(Tenant.Id, name);
        Db.Roles.Add(role);
        foreach (var key in keys)
        {
            var permission = Db.Permissions.Local.First(item => item.Key == key);
            Db.RolePermissions.Add(new RolePermission(role.Id, permission.Id));
        }

        return role;
    }

    public Role AddSystemRole(string name)
    {
        var role = new Role(Tenant.Id, name, $"{name} (system)", isSystemRole: true);
        Db.Roles.Add(role);
        if (PermissionResolver.IsAdminOrSuperAdminName(name))
        {
            foreach (var entry in PermissionCatalog.All)
            {
                var permission = Db.Permissions.Local.FirstOrDefault(item => item.Key == entry.Key);
                if (permission is null)
                {
                    continue;
                }

                Db.RolePermissions.Add(new RolePermission(role.Id, permission.Id));
            }
        }
        else if (name.Equals(SystemRoles.User, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var key in PermissionCatalog.DefaultUserKeys)
            {
                var permission = Db.Permissions.Local.First(item => item.Key == key);
                Db.RolePermissions.Add(new RolePermission(role.Id, permission.Id));
            }
        }

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

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
