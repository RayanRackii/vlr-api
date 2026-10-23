using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
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
using Platform.Api.Modules.ResourceVisuals;
using Platform.Api.Services.Trial;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Persistence.Seed;

namespace Platform.Api.Tests.ResourceVisuals;

public sealed class ResourceVisualHttpTests
{
    [Fact]
    public async Task Tenant_super_admin_and_platform_permission_claims_do_not_grant_platform_catalog_access()
    {
        using var host = await Start(); using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Email", "tenant-admin@test.local");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/resource-visual-templates")).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Test-Email"); client.DefaultRequestHeaders.Add("X-Test-Email", "platform@test.local");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/resource-visual-templates")).StatusCode);
    }
    [Fact]
    public async Task Public_art_is_svg_with_immutable_cache_etag_and_no_draft_visibility()
    {
        using var host = await Start(); using var client = host.GetTestClient();
        var revision = ResourceVisualCatalogSeed.Revisions[0]; var path = $"/api/public/resource-visual-revisions/{revision.Id}/art.svg";
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("image/svg+xml", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("immutable", response.Headers.CacheControl!.ToString()); Assert.Contains("floor", await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.TryAddWithoutValidation("If-None-Match", response.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.NotModified, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/public/resource-visual-revisions/{Guid.NewGuid()}/art.svg")).StatusCode);
    }
    private static async Task<IHost> Start()
    {
        var database = Guid.NewGuid().ToString();
        using (var db = InMemoryAppDb.Create(new FakeTenantProvider(), database))
        {
            db.AddRange(ResourceVisualCatalogSeed.Tags); db.AddRange(ResourceVisualCatalogSeed.Templates); db.AddRange(ResourceVisualCatalogSeed.Revisions); db.AddRange(ResourceVisualCatalogSeed.TemplateTags); await db.SaveChangesAsync();
        }
        return await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer(); web.ConfigureServices(services =>
            {
                services.AddLogging(); services.AddSingleton<ITenantProvider, FakeTenantProvider>(); services.AddSingleton<ITrialGuard, FakeTrialGuard>();
                services.AddScoped<AppDbContext>(sp => InMemoryAppDb.Create(sp.GetRequiredService<ITenantProvider>(), database));
                services.Configure<PlatformAdminOptions>(options => options.Emails = ["platform@test.local"]);
                services.AddSingleton<IPlatformAdminChecker, PlatformAdminChecker>(); services.AddSingleton<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
                services.AddAuthorization(options => options.AddRolvixPolicies()); services.AddScoped<Platform.Core.Domain.Contracts.ResourceVisuals.IRentalVisualDefaults, Platform.Api.Modules.Rentals.Services.RentalVisualDefaults>(); services.AddResourceVisuals();
                services.AddAuthentication(SupabaseJwtBearerDefaults.AuthenticationScheme).AddScheme<AuthenticationSchemeOptions, TestAuthentication>(SupabaseJwtBearerDefaults.AuthenticationScheme, _ => { });
                services.AddControllers().ConfigureApplicationPartManager(manager =>
                {
                    manager.ApplicationParts.Clear();
                    foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToArray()) manager.FeatureProviders.Remove(provider);
                    manager.ApplicationParts.Add(new AssemblyPart(typeof(AdminResourceVisualTemplatesController).Assembly)); manager.FeatureProviders.Add(new SelectedControllers());
                });
            });
            web.Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); });
        }).StartAsync();
    }
    private sealed class SelectedControllers : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) => typeInfo.AsType() == typeof(AdminResourceVisualTemplatesController) || typeInfo.AsType() == typeof(PublicResourceVisualArtController);
    }
    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Email", out var email)) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", email.ToString()), new Claim(ClaimTypes.Role, "SUPER_ADMIN"), new Claim("permission", ResourceVisualPermissions.TemplatesManage)], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
