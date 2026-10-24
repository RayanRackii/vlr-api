using Platform.Api.Services.Tenancy;
using System.Net;
using System.Reflection;
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
using Platform.Api.Modules.Admin.Controllers;
using Platform.Api.Modules.Admin.Services;
using Platform.Api.Modules.Customers.Controllers;
using Platform.Api.Modules.Customers.Services;
using Platform.Api.Modules.Users.Controllers;
using Platform.Api.Modules.Users.Services;
using Platform.Api.Notifications;
using Platform.Api.Services.Trial;
using Platform.Api.Storage;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Tests.Admin;

public sealed class PlatformAdminStaleTenantJwtTests
{
    private const string AdminEmail = "super@rolvix.test";
    private const string AuthId = "auth-super-1";

    [Fact]
    public async Task Enter_then_exit_ignores_stale_access_token_tenant()
    {
        var seed = await SeedAsync();
        using var host = await StartHostAsync(seed.DatabaseName);
        var auth = host.Services.GetRequiredService<FakeSupabaseAuthAdminClient>();

        var before = await GetAsync(host, "/api/users/me", seed.TenantA);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        using (var beforeDoc = JsonDocument.Parse(await before.Content.ReadAsStringAsync()))
        {
            Assert.Equal(JsonValueKind.Null, beforeDoc.RootElement.GetProperty("tenantId").ValueKind);
            Assert.Equal("SUPER_ADMIN", beforeDoc.RootElement.GetProperty("role").GetString());
        }

        await AssertNoTenantDataAsync(host, seed.TenantA);

        await auth.UpdateUserAppMetadataAsync(AuthId, seed.TenantB, CancellationToken.None);
        var foreign = await GetAsync(host, "/api/users/me", seed.TenantA);
        using (var foreignDoc = JsonDocument.Parse(await foreign.Content.ReadAsStringAsync()))
        {
            Assert.Equal(JsonValueKind.Null, foreignDoc.RootElement.GetProperty("tenantId").ValueKind);
        }

        await AssertNoTenantDataAsync(host, seed.TenantA);

        var enter = await PostAsync(host, $"/api/admin/tenants/{seed.TenantA}/enter", seed.TenantA);
        var enterBody = await enter.Content.ReadAsStringAsync();
        Assert.True(enter.StatusCode == HttpStatusCode.OK, enterBody);
        using (var enterDoc = JsonDocument.Parse(enterBody))
        {
            Assert.Equal(seed.TenantA, enterDoc.RootElement.GetProperty("tenantId").GetGuid());
        }

        var inside = await GetAsync(host, "/api/users/me", seed.TenantA);
        Assert.Equal(HttpStatusCode.OK, inside.StatusCode);
        using (var insideDoc = JsonDocument.Parse(await inside.Content.ReadAsStringAsync()))
        {
            Assert.Equal(seed.TenantA, insideDoc.RootElement.GetProperty("tenantId").GetGuid());
        }

        var customers = await GetAsync(host, "/api/customers", seed.TenantA);
        Assert.Equal(HttpStatusCode.OK, customers.StatusCode);
        var customerBody = await customers.Content.ReadAsStringAsync();
        Assert.Contains("Ana Portal", customerBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Bruno Other", customerBody, StringComparison.Ordinal);

        var exit = await PostAsync(host, "/api/admin/tenants/exit", seed.TenantA);
        Assert.Equal(HttpStatusCode.NoContent, exit.StatusCode);

        var after = await GetAsync(host, "/api/users/me", seed.TenantA);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        using (var afterDoc = JsonDocument.Parse(await after.Content.ReadAsStringAsync()))
        {
            Assert.Equal(JsonValueKind.Null, afterDoc.RootElement.GetProperty("tenantId").ValueKind);
            Assert.Equal("SUPER_ADMIN", afterDoc.RootElement.GetProperty("role").GetString());
        }

        await AssertNoTenantDataAsync(host, seed.TenantA);
        Assert.Null(await auth.GetUserTenantAppMetadataAsync(AuthId, CancellationToken.None));
    }

    private static async Task AssertNoTenantDataAsync(IHost host, Guid staleTenantId)
    {
        var response = await GetAsync(host, "/api/customers", staleTenantId);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Tenant context is required.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Ana Portal", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Bruno Other", body, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> GetAsync(IHost host, string path, Guid staleTenantId)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(StaleTokenHandler.UserHeader, AdminEmail);
        client.DefaultRequestHeaders.Add(StaleTokenHandler.TenantHeader, staleTenantId.ToString());
        return await client.GetAsync(path);
    }

    private static async Task<HttpResponseMessage> PostAsync(IHost host, string path, Guid staleTenantId)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(StaleTokenHandler.UserHeader, AdminEmail);
        client.DefaultRequestHeaders.Add(StaleTokenHandler.TenantHeader, staleTenantId.ToString());
        return await client.PostAsync(path, content: null);
    }

    private static async Task<Seed> SeedAsync()
    {
        var databaseName = $"stale-jwt-{Guid.NewGuid():N}";
        var provider = new FakeTenantProvider();
        await using var db = InMemoryAppDb.Create(provider, databaseName);
        var tenantA = new Tenant("Club A", "11111111000191", subdomain: "cluba");
        var tenantB = new Tenant("Club B", "22222222000191", subdomain: "clubb");
        db.Tenants.AddRange(tenantA, tenantB);
        db.Customers.AddRange(
            new Customer
            {
                TenantId = tenantA.Id,
                Name = "Ana Portal",
                Email = "ana.portal@club.test",
            },
            new Customer
            {
                TenantId = tenantB.Id,
                Name = "Bruno Other",
                Email = "bruno.other@club.test",
            });
        await db.SaveChangesAsync();
        return new Seed(databaseName, tenantA.Id, tenantB.Id);
    }

    private static Task<IHost> StartHostAsync(string databaseName)
    {
        var auth = new FakeSupabaseAuthAdminClient { EmailLookupUserId = AuthId };
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
                    services.AddSingleton(auth);
                    services.AddSingleton<ISupabaseAuthAdminClient>(auth);
                    services.Configure<PlatformAdminOptions>(options => options.Emails = [AdminEmail]);
                    services.AddSingleton<IPlatformAdminChecker, PlatformAdminChecker>();
                    services.AddSingleton<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
                    services.AddSingleton<NotificationQueue>();
                    services.AddSingleton(Options.Create(new StorageOptions()));
                    services.AddSingleton<IStorageProvider, RecordingStorageProvider>();
                    services.AddSingleton<ITrialGuard, FakeTrialGuard>();
                    services.AddSingleton<ITenantAccessBootstrapper, FakeTenantAccessBootstrapper>();
                    services.AddSingleton<ITenantUserAdminService, FakeTenantUserAdminService>();
                    services.AddScoped<AmbientTenantContext>();
                    services.AddScoped<PlatformAdminTenantSession>();
                    services.AddScoped<ITenantProvider, HttpContextTenantProvider>();
                    services.AddScoped<AppDbContext>(sp =>
                        InMemoryAppDb.Create(sp.GetRequiredService<ITenantProvider>(), databaseName));
                    services.AddScoped<ITenantModuleAccessor, TenantModuleAccessor>();
                    services.AddScoped<IPermissionResolver, PermissionResolver>();
                    services.AddScoped<IRbacGrantGuard, RbacGrantGuard>();
                    services.AddScoped<IRbacActorAccessor, RbacActorAccessor>();
                    services.AddScoped<IPlatformAdminMembershipService, PlatformAdminMembershipService>();
                    services.AddScoped<ITenantProvisioner, TenantProvisioner>();
                    services.AddScoped<ITenantPurger, TenantPurger>();
                    services.AddScoped<IAdminTenantService, AdminTenantService>();
                    services.AddScoped<IUserDirectoryService, UserDirectoryService>();
                    services.AddScoped<ITenantCustomerDirectory, TenantCustomerDirectory>();
                    services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
                    services.AddAuthorization(options => options.AddRolvixPolicies());
                    services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
                    services.AddSingleton<IAuthorizationMiddlewareResultHandler, JsonForbiddenAuthorizationResultHandler>();
                    services
                        .AddAuthentication(SupabaseJwtBearerDefaults.AuthenticationScheme)
                        .AddScheme<AuthenticationSchemeOptions, StaleTokenHandler>(
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

                            manager.ApplicationParts.Add(new AssemblyPart(typeof(AdminTenantsController).Assembly));
                            manager.FeatureProviders.Add(new SelectedControllers(
                                typeof(AdminTenantsController),
                                typeof(UsersController),
                                typeof(TenantCustomersController)));
                        });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseMiddleware<PlatformAdminTenantSessionMiddleware>();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .Start();
        return Task.FromResult(host);
    }

    private sealed record Seed(string DatabaseName, Guid TenantA, Guid TenantB);

    private sealed class SelectedControllers(params Type[] controllerTypes) : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) =>
            controllerTypes.Contains(typeInfo.AsType());
    }

    private sealed class StaleTokenHandler(
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
                new("sub", AuthId),
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
