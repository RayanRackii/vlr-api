using System.Net;
using System.Net.Http.Json;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Modules.Admin.Controllers;
using Platform.Api.Modules.Admin.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Admin;

/// <summary>
/// Guard rails of <c>GET /api/admin/tenants/{id}/modules/{module}/data-purge/preview</c> that never
/// reach the table counts: platform admin only, unknown tenant 404, unknown or non-purgeable module
/// 400 with a stable code. The counts themselves run on PostgreSQL (ModuleDataPurgePreviewPostgresTests).
/// </summary>
public sealed class ModuleDataPurgePreviewHttpTests
{
    private const string AdminEmail = "admin@rolvix.test";

    private static string Url(Guid tenantId, string module) =>
        $"/api/admin/tenants/{tenantId}/modules/{module}/data-purge/preview";

    [Fact]
    public async Task Only_platform_admins_can_preview()
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider());
        var tenant = await SeedTenantAsync(db);
        using var host = StartHost(db);
        var url = Url(tenant.Id, "rentals");

        using var anonymous = host.GetTestClient();
        using var anonymousResponse = await anonymous.GetAsync(url);
        Assert.Contains(
            anonymousResponse.StatusCode,
            new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });

        // A tenant admin (not a platform admin) is refused, whatever the tenant in the URL.
        using var tenantAdmin = host.GetTestClient();
        tenantAdmin.DefaultRequestHeaders.Add("X-Test-Email", "owner@club.test");
        using var forbidden = await tenantAdmin.GetAsync(url);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task An_unknown_tenant_is_not_found()
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider());
        using var host = StartHost(db);
        using var client = AdminClient(host);

        using var response = await client.GetAsync(Url(Guid.NewGuid(), "rentals"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("rental-x")]
    public async Task An_unknown_module_is_a_bad_request(string module)
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider());
        var tenant = await SeedTenantAsync(db);
        using var host = StartHost(db);
        using var client = AdminClient(host);

        using var response = await client.GetAsync(Url(tenant.Id, module));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("module.unknown", body.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("inventory")]
    [InlineData("maintenance")]
    [InlineData("core")]
    [InlineData("assets")]
    [InlineData("Core")]
    public async Task A_module_without_purgeable_data_is_a_bad_request(string module)
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider());
        var tenant = await SeedTenantAsync(db);
        using var host = StartHost(db);
        using var client = AdminClient(host);

        using var response = await client.GetAsync(Url(tenant.Id, module));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("module.not_purgeable", body.GetProperty("code").GetString());
    }

    private static async Task<Tenant> SeedTenantAsync(AppDbContext db)
    {
        var tenant = new Tenant("Preview club", "33333333000191", subdomain: "preview-club");
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant;
    }

    private static HttpClient AdminClient(IHost host)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Email", AdminEmail);
        return client;
    }

    private static IHost StartHost(AppDbContext db) => new HostBuilder()
        .ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton(db);
                services.AddSingleton<ITenantProvider>(new FakeTenantProvider());
                services.AddScoped<IModuleDataPurgeService, ModuleDataPurgeService>();
                services.AddSingleton<IPlatformAdminChecker>(new FakePlatformAdminChecker(AdminEmail));
                services.AddSingleton<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                services.AddAuthorization(options => options.AddPolicy(
                    SupabaseAuthenticationExtensions.PlatformAdminPolicy,
                    policy => policy.Requirements.Add(new PlatformAdminRequirement())));
                services.AddControllers().ConfigureApplicationPartManager(manager =>
                {
                    manager.ApplicationParts.Clear();
                    manager.FeatureProviders.Clear();
                    manager.ApplicationParts.Add(new AssemblyPart(typeof(AdminModuleDataPurgeController).Assembly));
                    manager.FeatureProviders.Add(new PurgeControllerFeatureProvider());
                });
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            });
        }).Start();

    private sealed class PurgeControllerFeatureProvider : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) =>
            typeInfo.AsType() == typeof(AdminModuleDataPurgeController);
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Email", out var email))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity([new Claim("email", email.ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
