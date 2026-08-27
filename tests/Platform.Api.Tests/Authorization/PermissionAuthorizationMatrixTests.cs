using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Authorization;

public sealed class PermissionAuthorizationMatrixTests
{
    private const string AdminEmail = "admin@rolvix.test";

    [Fact]
    public async Task Permission_policy_includes_default_b2b_and_rejects_customer()
    {
        await using var harness = await AuthzMatrixHarness.CreateAsync();
        var customer = AuthenticatedPrincipal(
            new Claim(ClaimTypes.Role, AuthRoles.Customer),
            new Claim(CustomerClaimTypes.Role, AuthRoles.Customer),
            new Claim(CustomerClaimTypes.CustomerId, Guid.NewGuid().ToString()),
            new Claim("email", "member@club.test"));

        var result = await harness.Authorization.AuthorizeAsync(
            customer,
            PermissionPolicies.Name(Permissions.Core.DashboardRead));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task User_without_permission_is_denied()
    {
        await using var harness = await AuthzMatrixHarness.CreateAsync();
        var user = harness.SeedUserWithKeys("no-perm", Permissions.Core.UnitsRead);
        var principal = AuthenticatedPrincipal(
            new Claim("sub", user.SupabaseAuthId),
            new Claim("email", user.Email));

        var result = await harness.Authorization.AuthorizeAsync(
            principal,
            PermissionPolicies.Name(Permissions.Core.DashboardRead));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task User_with_permission_is_allowed()
    {
        await using var harness = await AuthzMatrixHarness.CreateAsync();
        var user = harness.SeedUserWithKeys("has-perm", Permissions.Core.DashboardRead);
        var principal = AuthenticatedPrincipal(
            new Claim("sub", user.SupabaseAuthId),
            new Claim("email", user.Email));

        var result = await harness.Authorization.AuthorizeAsync(
            principal,
            PermissionPolicies.Name(Permissions.Core.DashboardRead));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Jwt_sub_is_resolved_as_supabase_auth_id_not_user_id()
    {
        await using var harness = await AuthzMatrixHarness.CreateAsync();
        var user = harness.SeedUserWithKeys("auth-sub-1", Permissions.Core.DashboardRead);
        var principalUsingUserId = AuthenticatedPrincipal(
            new Claim("sub", user.Id.ToString()),
            new Claim("email", user.Email));
        var principalUsingAuthId = AuthenticatedPrincipal(
            new Claim("sub", user.SupabaseAuthId),
            new Claim("email", user.Email));

        Assert.False(
            (await harness.Authorization.AuthorizeAsync(
                principalUsingUserId,
                PermissionPolicies.Name(Permissions.Core.DashboardRead))).Succeeded);
        Assert.True(
            (await harness.Authorization.AuthorizeAsync(
                principalUsingAuthId,
                PermissionPolicies.Name(Permissions.Core.DashboardRead))).Succeeded);
    }

    [Fact]
    public async Task Platform_admin_with_tenant_is_allowed_without_membership()
    {
        await using var harness = await AuthzMatrixHarness.CreateAsync();
        var principal = AuthenticatedPrincipal(
            new Claim("email", AdminEmail),
            new Claim(ClaimTypes.Email, AdminEmail));

        var result = await harness.Authorization.AuthorizeAsync(
            principal,
            PermissionPolicies.Name(Permissions.Core.RolesManage));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Platform_admin_without_tenant_is_denied_tenant_permission()
    {
        await using var harness = await AuthzMatrixHarness.CreateAsync(withTenant: false);
        var principal = AuthenticatedPrincipal(
            new Claim("email", AdminEmail),
            new Claim(ClaimTypes.Email, AdminEmail));

        var result = await harness.Authorization.AuthorizeAsync(
            principal,
            PermissionPolicies.Name(Permissions.Core.RolesManage));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Customer_and_platform_admin_named_policies_still_delegate()
    {
        await using var harness = await AuthzMatrixHarness.CreateAsync();
        var customer = AuthenticatedPrincipal(
            new Claim(ClaimTypes.Role, AuthRoles.Customer),
            new Claim(CustomerClaimTypes.Role, AuthRoles.Customer),
            new Claim(CustomerClaimTypes.CustomerId, Guid.NewGuid().ToString()));
        var platformAdmin = AuthenticatedPrincipal(
            new Claim("email", AdminEmail),
            new Claim(ClaimTypes.Email, AdminEmail));

        Assert.True((await harness.Authorization.AuthorizeAsync(customer, "Customer")).Succeeded);
        Assert.True(
            (await harness.Authorization.AuthorizeAsync(
                platformAdmin,
                SupabaseAuthenticationExtensions.PlatformAdminPolicy)).Succeeded);
        Assert.False((await harness.Authorization.AuthorizeAsync(customer, "PlatformAdmin")).Succeeded);
    }

    private static ClaimsPrincipal AuthenticatedPrincipal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Test"));

    private sealed class AuthzMatrixHarness : IAsyncDisposable
    {
        private AuthzMatrixHarness(
            ServiceProvider services,
            AppDbContext db,
            Tenant tenant,
            FakeTenantProvider tenantProvider)
        {
            Services = services;
            Db = db;
            Tenant = tenant;
            TenantProvider = tenantProvider;
            Authorization = services.GetRequiredService<IAuthorizationService>();
        }

        public ServiceProvider Services { get; }

        public AppDbContext Db { get; }

        public Tenant Tenant { get; }

        public FakeTenantProvider TenantProvider { get; }

        public IAuthorizationService Authorization { get; }

        public static async Task<AuthzMatrixHarness> CreateAsync(bool withTenant = true)
        {
            var tenant = new Tenant("Authz Club", "33333333000191");
            var tenantProvider = new FakeTenantProvider
            {
                TenantId = withTenant ? tenant.Id : null,
            };
            var db = InMemoryAppDb.Create(tenantProvider);
            db.Tenants.Add(tenant);
            foreach (var entry in PermissionCatalog.All)
            {
                db.Permissions.Add(new Permission(entry.Key, entry.Name, entry.Description, entry.ModuleKey));
            }

            await db.SaveChangesAsync();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOptions();
            services.Configure<PlatformAdminOptions>(options => options.Emails = [AdminEmail]);
            services.AddSingleton<IPlatformAdminChecker>(new FakePlatformAdminChecker(AdminEmail));
            services.AddSingleton<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
            services.AddSingleton<ITenantProvider>(tenantProvider);
            services.AddSingleton(db);
            services.AddSingleton<AppDbContext>(db);
            services.AddSingleton<IPermissionResolver>(_ =>
                new PermissionResolver(db, NullLogger<PermissionResolver>.Instance));
            services.AddSingleton<IAuthorizationHandler>(sp =>
                new PermissionAuthorizationHandler(
                    sp.GetRequiredService<IPermissionResolver>(),
                    tenantProvider,
                    sp.GetRequiredService<IPlatformAdminChecker>(),
                    db,
                    NullLogger<PermissionAuthorizationHandler>.Instance));
            services.AddAuthorization(options => options.AddRolvixPolicies());
            services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();

            var provider = services.BuildServiceProvider();
            return new AuthzMatrixHarness(provider, db, tenant, tenantProvider);
        }

        public User SeedUserWithKeys(string supabaseAuthId, params string[] keys)
        {
            var role = new Role(Tenant.Id, $"role-{supabaseAuthId}");
            Db.Roles.Add(role);
            foreach (var key in keys)
            {
                var permission = Db.Permissions.Local.First(p => p.Key == key);
                Db.RolePermissions.Add(new RolePermission(role.Id, permission.Id));
            }

            var user = new User(Tenant.Id, supabaseAuthId, supabaseAuthId, $"{supabaseAuthId}@test.com");
            Db.Users.Add(user);
            Db.UserRoles.Add(new UserRole(user.Id, role.Id));
            Db.SaveChanges();
            return user;
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await Db.DisposeAsync();
        }
    }
}
