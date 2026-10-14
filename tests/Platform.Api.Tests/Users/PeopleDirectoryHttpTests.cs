using System.Net;
using System.Reflection;
using System.Text;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Modules.Customers.Controllers;
using Platform.Api.Modules.Customers.Services;
using Platform.Api.Modules.Users.Controllers;
using Platform.Api.Modules.Users.Services;
using Platform.Api.Notifications;
using Platform.Api.Services.Trial;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Users;

public sealed class PeopleDirectoryHttpTests
{
    private const string PlatformEmail = "platform@club.test";
    private const string AdminEmail = "admin@club.test";
    private const string StaffEmail = "staff@club.test";
    private const string ViewerEmail = "viewer@club.test";
    private const string AssignerEmail = "assigner@club.test";
    private const string SecretHash = "secret-hash-should-not-leak";
    private const string SecretPhone = "11988887777";
    private const string SecretDocument = "12345678901";

    [Fact]
    public async Task Platform_admin_without_tenant_gets_401_and_no_rows()
    {
        var seed = await SeedAsync();
        using var host = await StartHostAsync(seed.DatabaseName);
        var response = await GetAsync(host, "/api/customers", PlatformEmail, tenantId: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Ana Portal", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Bruno Other", body, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretHash, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Staff_and_system_admin_cannot_list_portal_customers()
    {
        var seed = await SeedAsync();
        using var host = await StartHostAsync(seed.DatabaseName);

        var staff = await GetAsync(host, "/api/customers", StaffEmail, seed.TenantA);
        var admin = await GetAsync(host, "/api/customers", AdminEmail, seed.TenantA);

        Assert.Equal(HttpStatusCode.Forbidden, staff.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);
        Assert.Equal(
            RbacErrorCodes.Forbidden,
            JsonDocument.Parse(await staff.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Explicit_grant_and_platform_admin_list_only_the_current_tenant()
    {
        var seed = await SeedAsync();
        using var host = await StartHostAsync(seed.DatabaseName);

        foreach (var email in new[] { ViewerEmail, PlatformEmail })
        {
            var response = await GetAsync(host, "/api/customers?page=1&pageSize=20", email, seed.TenantA);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            AssertNoSecrets(body);
            using var doc = JsonDocument.Parse(body);
            Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt32());
            var names = doc.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("name").GetString())
                .ToArray();
            Assert.Equal(2, names.Length);
            Assert.Equal("Ana Portal", names[0]);
            Assert.Equal("Zoe Portal", names[1]);
            Assert.True(doc.RootElement.GetProperty("items")[0].GetProperty("emailVerified").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("items")[1].GetProperty("emailVerified").GetBoolean());
            Assert.DoesNotContain(
                doc.RootElement.GetProperty("items").EnumerateArray(),
                item => item.GetProperty("id").GetGuid() == seed.OtherCustomerId);
        }
    }

    [Fact]
    public async Task Customer_page_does_not_include_another_tenant_on_later_pages()
    {
        var seed = await SeedAsync();
        using var host = await StartHostAsync(seed.DatabaseName);
        var response = await GetAsync(host, "/api/customers?page=2&pageSize=1", ViewerEmail, seed.TenantA);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt32());
        var item = Assert.Single(doc.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("Zoe Portal", item.GetProperty("name").GetString());
        Assert.NotEqual(seed.OtherCustomerId, item.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Short_search_and_oversized_page_are_rejected()
    {
        var seed = await SeedAsync();
        using var host = await StartHostAsync(seed.DatabaseName);

        var shortSearch = await GetAsync(host, "/api/customers?q=a", ViewerEmail, seed.TenantA);
        var hugePage = await GetAsync(host, "/api/users?pageSize=51", ViewerEmail, seed.TenantA);

        Assert.Equal(HttpStatusCode.BadRequest, shortSearch.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, hugePage.StatusCode);
    }

    [Fact]
    public async Task Users_list_without_tenant_is_forbidden()
    {
        var seed = await SeedAsync();
        using var host = await StartHostAsync(seed.DatabaseName);

        var viewer = await GetAsync(host, "/api/users", ViewerEmail, tenantId: null);
        var platform = await GetAsync(host, "/api/users", PlatformEmail, tenantId: null);
        var viewerBody = await viewer.Content.ReadAsStringAsync();
        var platformBody = await platform.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, platform.StatusCode);
        Assert.Equal(
            RbacErrorCodes.Forbidden,
            JsonDocument.Parse(viewerBody).RootElement.GetProperty("error").GetString());
        Assert.Equal(
            RbacErrorCodes.Forbidden,
            JsonDocument.Parse(platformBody).RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain(StaffEmail, viewerBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Assigner_without_customer_read_cannot_grant_that_role_to_self()
    {
        var seeded = await SeedAssignerAsync();
        using var host = await StartHostAsync(seeded.DatabaseName);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, AssignerEmail);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, seeded.TenantId.ToString());

        var payload = JsonSerializer.Serialize(new { roleIds = new[] { seeded.CustomersRoleId } });
        var response = await client.PutAsync(
            $"/api/users/{seeded.AssignerId}/roles",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            RbacErrorCodes.PrivilegeEscalationBlocked,
            JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Staff_list_excludes_platform_admins_and_portal_customers()
    {
        var seed = await SeedAsync();
        using var host = await StartHostAsync(seed.DatabaseName);
        var response = await GetAsync(host, "/api/users", ViewerEmail, seed.TenantA);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var emails = doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("email").GetString())
            .ToArray();
        Assert.Contains(StaffEmail, emails);
        Assert.Contains(AdminEmail, emails);
        Assert.Contains(ViewerEmail, emails);
        Assert.DoesNotContain(PlatformEmail, emails);
        Assert.DoesNotContain("ana.portal@club.test", emails);
        Assert.DoesNotContain("bruno.other@club.test", emails);
        Assert.Equal(3, doc.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Platform_admin_session_includes_customer_read_and_system_admin_does_not()
    {
        var seed = await SeedAsync();
        using var host = await StartHostAsync(seed.DatabaseName);

        var platform = await GetAsync(host, "/api/users/me", PlatformEmail, seed.TenantA);
        var admin = await GetAsync(host, "/api/users/me", AdminEmail, seed.TenantA);
        Assert.Equal(HttpStatusCode.OK, platform.StatusCode);
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);

        using var platformDoc = JsonDocument.Parse(await platform.Content.ReadAsStringAsync());
        using var adminDoc = JsonDocument.Parse(await admin.Content.ReadAsStringAsync());
        var platformKeys = platformDoc.RootElement.GetProperty("permissions").EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        var adminKeys = adminDoc.RootElement.GetProperty("permissions").EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        Assert.Contains(Permissions.Core.CustomersRead, platformKeys);
        Assert.DoesNotContain(Permissions.Core.CustomersRead, adminKeys);
    }

    private static void AssertNoSecrets(string body)
    {
        Assert.DoesNotContain(SecretHash, body, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretPhone, body, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretDocument, body, StringComparison.Ordinal);
        Assert.DoesNotContain("passwordHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"phone\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"document\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"cpf\"", body, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<HttpResponseMessage> GetAsync(
        IHost host,
        string path,
        string email,
        Guid? tenantId)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, email);
        if (tenantId is Guid id)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, id.ToString());
        }

        return await client.GetAsync(path);
    }

    private static async Task<Seed> SeedAsync()
    {
        var databaseName = $"people-{Guid.NewGuid():N}";
        var provider = new FakeTenantProvider();
        await using var db = InMemoryAppDb.Create(provider, databaseName);
        var tenantA = new Tenant("Club A", "11111111000191", subdomain: "cluba");
        var tenantB = new Tenant("Club B", "22222222000191", subdomain: "clubb");
        db.Tenants.AddRange(tenantA, tenantB);

        var customersRead = new Permission(Permissions.Core.CustomersRead, "Read portal customers");
        var usersRead = new Permission(Permissions.Core.UsersRead, "Read users");
        db.Permissions.AddRange(customersRead, usersRead);

        var adminRole = new Role(tenantA.Id, SystemRoles.Admin, "Admin (system)", isSystemRole: true);
        var staffRole = new Role(tenantA.Id, "Staff", "Staff");
        var viewerRole = new Role(tenantA.Id, "Viewer", "Viewer");
        db.Roles.AddRange(adminRole, staffRole, viewerRole);
        db.RolePermissions.Add(new RolePermission(staffRole.Id, usersRead.Id));
        db.RolePermissions.Add(new RolePermission(viewerRole.Id, usersRead.Id));
        db.RolePermissions.Add(new RolePermission(viewerRole.Id, customersRead.Id));

        var admin = new User(tenantA.Id, AdminEmail, "Admin User", AdminEmail);
        var staff = new User(tenantA.Id, StaffEmail, "Staff User", StaffEmail);
        var viewer = new User(tenantA.Id, ViewerEmail, "Viewer User", ViewerEmail);
        var platform = new User(tenantA.Id, PlatformEmail, "Platform User", PlatformEmail);
        db.Users.AddRange(admin, staff, viewer, platform);
        db.UserRoles.Add(new UserRole(admin.Id, adminRole.Id));
        db.UserRoles.Add(new UserRole(staff.Id, staffRole.Id));
        db.UserRoles.Add(new UserRole(viewer.Id, viewerRole.Id));

        var ana = new Customer
        {
            TenantId = tenantA.Id,
            Name = "Ana Portal",
            Email = "ana.portal@club.test",
            EmailVerifiedAt = DateTimeOffset.Parse("2026-10-01T00:00:00Z"),
            PasswordHash = SecretHash,
            Phone = SecretPhone,
            Document = SecretDocument,
            Cpf = SecretDocument,
        };
        var zoe = new Customer
        {
            TenantId = tenantA.Id,
            Name = "Zoe Portal",
            Email = "zoe.portal@club.test",
            PasswordHash = SecretHash,
            Phone = "11977776666",
            Document = "98765432100",
        };
        var bruno = new Customer
        {
            TenantId = tenantB.Id,
            Name = "Bruno Other",
            Email = "bruno.other@club.test",
            PasswordHash = SecretHash,
            Phone = SecretPhone,
            Document = SecretDocument,
        };
        db.Customers.AddRange(ana, zoe, bruno);
        await db.SaveChangesAsync();
        return new Seed(databaseName, tenantA.Id, bruno.Id);
    }

    private static async Task<AssignerSeed> SeedAssignerAsync()
    {
        var databaseName = $"people-assign-{Guid.NewGuid():N}";
        var provider = new FakeTenantProvider();
        await using var db = InMemoryAppDb.Create(provider, databaseName);
        var tenant = new Tenant("Club A", "11111111000191", subdomain: "cluba");
        db.Tenants.Add(tenant);

        var assignRoles = new Permission(Permissions.Core.UsersAssignRoles, "Assign roles");
        var customersRead = new Permission(Permissions.Core.CustomersRead, "Read portal customers");
        db.Permissions.AddRange(assignRoles, customersRead);

        var assignerRole = new Role(tenant.Id, "RoleAssigner", "Assigns roles");
        var portalRole = new Role(tenant.Id, "PortalReaders", "Reads customers");
        db.Roles.AddRange(assignerRole, portalRole);
        db.RolePermissions.Add(new RolePermission(assignerRole.Id, assignRoles.Id));
        db.RolePermissions.Add(new RolePermission(portalRole.Id, customersRead.Id));

        var assigner = new User(tenant.Id, AssignerEmail, "Assigner", AssignerEmail);
        db.Users.Add(assigner);
        db.UserRoles.Add(new UserRole(assigner.Id, assignerRole.Id));
        await db.SaveChangesAsync();

        return new AssignerSeed(databaseName, tenant.Id, assigner.Id, portalRole.Id);
    }

    private static Task<IHost> StartHostAsync(string databaseName)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddHttpContextAccessor();
                    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
                    services.AddSingleton<IHostEnvironment, FakeHostEnvironment>();
                    services.AddSingleton<NotificationQueue>();
                    services.AddSingleton<IPlatformAdminChecker>(_ => new FakePlatformAdminChecker(PlatformEmail));
                    services.AddScoped<AmbientTenantContext>();
                    services.AddScoped<ITenantProvider, HttpContextTenantProvider>();
                    services.AddScoped<AppDbContext>(sp =>
                        InMemoryAppDb.Create(sp.GetRequiredService<ITenantProvider>(), databaseName));
                    services.AddScoped<ITenantModuleAccessor, TenantModuleAccessor>();
                    services.AddScoped<IPermissionResolver, PermissionResolver>();
                    services.AddScoped<IRbacGrantGuard, RbacGrantGuard>();
                    services.AddSingleton<ITrialGuard, FakeTrialGuard>();
                    services.AddScoped<IRbacActorAccessor, RbacActorAccessor>();
                    services.AddScoped<IUserDirectoryService, UserDirectoryService>();
                    services.AddScoped<ITenantCustomerDirectory, TenantCustomerDirectory>();
                    services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
                    services.AddAuthorization(options => options.AddRolvixPolicies());
                    services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
                    services.AddSingleton<IAuthorizationMiddlewareResultHandler, JsonForbiddenAuthorizationResultHandler>();
                    services
                        .AddAuthentication(SupabaseJwtBearerDefaults.AuthenticationScheme)
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                            SupabaseJwtBearerDefaults.AuthenticationScheme,
                            _ => { });
                    services.AddControllers()
                        .ConfigureApplicationPartManager(manager =>
                        {
                            manager.ApplicationParts.Clear();
                            foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
                            {
                                manager.FeatureProviders.Remove(provider);
                            }

                            manager.ApplicationParts.Add(new AssemblyPart(typeof(UsersController).Assembly));
                            manager.FeatureProviders.Add(new SelectedControllers(
                                typeof(UsersController),
                                typeof(TenantCustomersController)));
                        });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .Start();
        return Task.FromResult(host);
    }

    private sealed record Seed(string DatabaseName, Guid TenantA, Guid OtherCustomerId);

    private sealed record AssignerSeed(
        string DatabaseName,
        Guid TenantId,
        Guid AssignerId,
        Guid CustomersRoleId);

    private sealed class SelectedControllers(params Type[] controllerTypes) : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) =>
            controllerTypes.Contains(typeInfo.AsType());
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string UserHeader = "X-Test-User";
        public const string TenantHeader = "X-Test-Tenant";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out var userValues)
                || string.IsNullOrWhiteSpace(userValues.ToString()))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var email = userValues.ToString();
            var claims = new List<Claim>
            {
                new("email", email),
                new(ClaimTypes.Email, email),
                new(ClaimTypes.Name, email),
                new("sub", email),
            };

            if (Request.Headers.TryGetValue(TenantHeader, out var tenantValues)
                && Guid.TryParse(tenantValues.ToString(), out var tenantId))
            {
                claims.Add(new Claim(
                    TenantClaimTypes.AppMetadata,
                    $"{{\"tenant_id\":\"{tenantId}\"}}"));
            }

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
