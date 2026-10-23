using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Modules.ResourceVisuals;
using Platform.Api.Services.Trial;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Persistence.Seed;

namespace Platform.Api.Tests.ResourceVisuals;

public sealed class ResourceVisualHttpRegressionTests
{
    [Theory]
    [InlineData(Permissions.Rentals.AssetsRead, HttpStatusCode.OK)]
    [InlineData(Permissions.Rentals.LayoutsRead, HttpStatusCode.OK)]
    [InlineData(Permissions.Rentals.AssetsWrite, HttpStatusCode.Forbidden)]
    [InlineData("", HttpStatusCode.Forbidden)]
    public async Task Picker_requires_assets_read_or_layouts_read(string permission, HttpStatusCode expected)
    {
        using var harness = await Harness.Start(); using var client = harness.TenantClient(permission);
        Assert.Equal(expected, (await client.GetAsync("/api/rental-assets/visual-templates")).StatusCode);
        Assert.Equal(expected, (await client.GetAsync("/api/rental-assets/visual-template-tags")).StatusCode);
    }

    [Fact]
    public async Task Bracket_and_repeated_filters_have_identical_facet_and_segment_semantics()
    {
        using var harness = await Harness.Start(); using var client = harness.TenantClient(Permissions.Rentals.AssetsRead);
        var sports = Tag("sports-courts"); var sand = Tag("sand"); var clay = Tag("clay"); var tennis = Tag("tennis");
        var repeated = await ReadPage(client, $"/api/rental-assets/visual-templates?segmentIds={sports}&tagIds={sand}&tagIds={clay}&tagIds={tennis}");
        var brackets = await ReadPage(client, $"/api/rental-assets/visual-templates?segmentIds[]={sports}&tagIds[]={sand}&tagIds[]={clay}&tagIds[]={tennis}");
        Assert.Equal(repeated.Items.Select(x => x.Id), brackets.Items.Select(x => x.Id));
        Assert.Equal("tennis-clay", Assert.Single(repeated.Items).Slug);
    }

    [Fact]
    public async Task Tenant_super_admin_cannot_manage_catalog_tags_or_explicit_tenant_segments()
    {
        using var harness = await Harness.Start(); using var client = harness.TenantClient(ResourceVisualPermissions.TemplatesManage, ResourceVisualPermissions.TagsManage, ResourceVisualPermissions.TenantSegmentsManage, ResourceVisualPermissions.SegmentsWrite);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/resource-visual-templates")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/resource-visual-tags")).StatusCode);
        var path = $"/api/admin/tenants/{harness.TenantB}/resource-segments";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(path, new SegmentsRequest([Tag("health")]))).StatusCode);
    }

    [Fact]
    public async Task Platform_admin_updates_explicit_tenant_despite_current_tenant_context()
    {
        using var harness = await Harness.Start(); using var admin = harness.PlatformClient(); using var tenant = harness.TenantClient(ResourceVisualPermissions.SegmentsRead);
        var response = await admin.PutAsJsonAsync($"/api/admin/tenants/{harness.TenantB}/resource-segments", new SegmentsRequest([Tag("health")]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Tag("health"), Assert.Single((await response.Content.ReadFromJsonAsync<VisualTagDto[]>())!).Id);
        var readOther = await admin.GetFromJsonAsync<VisualTagDto[]>($"/api/admin/tenants/{harness.TenantB}/resource-segments");
        Assert.Equal(Tag("health"), Assert.Single(readOther!).Id);
        Assert.Empty((await tenant.GetFromJsonAsync<VisualTagDto[]>("/api/rentals/resource-segments"))!);
    }

    [Fact]
    public async Task Tenant_segment_permissions_are_separate_and_cannot_replace_other_tenant_segments()
    {
        using var harness = await Harness.Start(); using var reader = harness.TenantClient(ResourceVisualPermissions.SegmentsRead); using var writer = harness.TenantClient(ResourceVisualPermissions.SegmentsWrite); using var picker = harness.TenantClient(Permissions.Rentals.AssetsRead);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync("/api/rentals/resource-segments", new SegmentsRequest([Tag("health")]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await writer.PutAsJsonAsync("/api/rentals/resource-segments", new SegmentsRequest([Tag("health")]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.GetAsync("/api/rentals/resource-segments")).StatusCode);
        var selected = await ReadPage(picker, "/api/rental-assets/visual-templates");
        Assert.Equal("consultation-room", Assert.Single(selected.Items).Slug);
        Assert.Equal(13, (await ReadPage(picker, "/api/rental-assets/visual-templates?allSegments=true")).Total);
    }

    [Theory]
    [InlineData("/api/rental-assets/visual-templates?page=invalid", "VISUAL_TEMPLATE_INVALID")]
    [InlineData("/api/rental-assets/visual-templates?tagIds=not-a-guid", "VISUAL_TEMPLATE_INVALID")]
    [InlineData("/api/rental-assets/visual-templates?tagIds[]=not-a-guid", "SEGMENT_INVALID")]
    public async Task Malformed_picker_queries_return_stable_errors(string path, string code)
    {
        using var harness = await Harness.Start(); using var client = harness.TenantClient(Permissions.Rentals.AssetsRead);
        await AssertError(await client.GetAsync(path), code);
    }

    [Fact]
    public async Task Malformed_admin_and_tenant_bodies_return_stable_errors()
    {
        using var harness = await Harness.Start(); using var admin = harness.PlatformClient(); using var tenant = harness.TenantClient(ResourceVisualPermissions.SegmentsWrite);
        await AssertError(await admin.PostAsync("/api/admin/resource-visual-templates", Json("{ invalid json")), "VISUAL_TEMPLATE_INVALID");
        await AssertError(await tenant.PutAsync("/api/rentals/resource-segments", Json("{\"segmentIds\":[\"not-a-guid\"]}")), "SEGMENT_INVALID");
        await AssertError(await admin.PutAsync($"/api/admin/tenants/{harness.TenantB}/resource-segments", Json("{ invalid json")), "SEGMENT_INVALID");
    }

    [Fact]
    public async Task Http_draft_publish_archive_retains_identical_public_art_and_rejects_editing_publication()
    {
        using var harness = await Harness.Start(); using var admin = harness.PlatformClient(); using var publicClient = harness.PublicClient(); using var picker = harness.TenantClient(Permissions.Rentals.AssetsRead);
        using var createdResponse = await admin.PostAsJsonAsync("/api/admin/resource-visual-templates", new TemplateRequest("http-test-room", new() { ["en"] = "HTTP test room" }, [], []));
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var template = (await createdResponse.Content.ReadFromJsonAsync<VisualTemplateDto>())!;
        var request = new RevisionRequest("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 100\"><rect width=\"100\" height=\"100\" fill=\"#AABBCC\"/></svg>", null, null, "0 0 100 100", 1, "#AABBCC", "square");
        using var draftResponse = await admin.PostAsJsonAsync($"/api/admin/resource-visual-templates/{template.Id}/revisions", request);
        Assert.Equal(HttpStatusCode.OK, draftResponse.StatusCode);
        var draft = (await draftResponse.Content.ReadFromJsonAsync<AdminVisualRevisionDto>())!;
        var artPath = $"/api/public/resource-visual-revisions/{draft.Metadata.Id}/art.svg";
        Assert.Equal(HttpStatusCode.NotFound, (await publicClient.GetAsync(artPath)).StatusCode);
        using var publication = await admin.PostAsync($"/api/admin/resource-visual-templates/{template.Id}/revisions/{draft.Metadata.Id}/publish", null);
        Assert.Equal(HttpStatusCode.OK, publication.StatusCode);
        var published = (await publication.Content.ReadFromJsonAsync<VisualRevisionDto>())!;
        Assert.NotNull(published.ArtUrl); Assert.Equal(64, published.ArtSha256.Length);
        using var before = await publicClient.GetAsync(artPath); Assert.Equal(HttpStatusCode.OK, before.StatusCode); var originalArt = await before.Content.ReadAsStringAsync();
        var originalEtag = before.Headers.ETag;
        var rejected = await admin.PutAsJsonAsync($"/api/admin/resource-visual-templates/{template.Id}/revisions/{draft.Metadata.Id}", request);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/resource-visual-templates/{template.Id}")).StatusCode);
        using var after = await publicClient.GetAsync(artPath); Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal(originalArt, await after.Content.ReadAsStringAsync()); Assert.Equal(originalEtag, after.Headers.ETag);
        Assert.DoesNotContain((await ReadPage(picker, "/api/rental-assets/visual-templates?allSegments=true")).Items, x => x.Id == template.Id);
    }

    private static Guid Tag(string slug) => ResourceVisualCatalogSeed.StableId("tag:" + slug);
    private static StringContent Json(string source) => new(source, Encoding.UTF8, "application/json");
    private static async Task<VisualPage> ReadPage(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path); Assert.Equal(HttpStatusCode.OK, response.StatusCode); return (await response.Content.ReadFromJsonAsync<VisualPage>())!;
    }
    private static async Task AssertError(HttpResponseMessage response, string code)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            Assert.Equal(code, json.RootElement.GetProperty("code").GetString()); Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("error").GetString()));
        }
    }

    private sealed class Harness(IHost host, Guid tenantB) : IDisposable
    {
        public Guid TenantB { get; } = tenantB;
        public HttpClient PublicClient() => host.GetTestClient();
        public HttpClient PlatformClient()
        {
            var client = PublicClient(); client.DefaultRequestHeaders.Add("X-Test-Email", "platform@test.local"); return client;
        }
        public HttpClient TenantClient(params string[] permissions)
        {
            var client = PublicClient(); client.DefaultRequestHeaders.Add("X-Test-Email", "tenant-admin@test.local"); client.DefaultRequestHeaders.Add("X-Test-Permissions", string.Join('|', permissions)); return client;
        }
        public void Dispose() => host.Dispose();
        public static async Task<Harness> Start()
        {
            var database = Guid.NewGuid().ToString(); var a = new Tenant("Current tenant", "a"); var b = new Tenant("Other tenant", "b"); var tenantProvider = new FakeTenantProvider { TenantId = a.Id };
            using (var db = InMemoryAppDb.Create(tenantProvider, database))
            {
                db.AddRange(a, b, new User(a.Id, "catalog-test-user", "Tenant administrator", "tenant-admin@test.local"), new TenantModule(a.Id, PlatformModules.Rentals, true), new TenantModule(b.Id, PlatformModules.Rentals, true));
                db.AddRange(ResourceVisualCatalogSeed.Tags); db.AddRange(ResourceVisualCatalogSeed.Templates); db.AddRange(ResourceVisualCatalogSeed.Revisions); db.AddRange(ResourceVisualCatalogSeed.TemplateTags); await db.SaveChangesAsync();
            }
            var host = await new HostBuilder().ConfigureWebHost(web =>
            {
                web.UseTestServer(); web.ConfigureServices(services =>
                {
                    services.AddLogging(); services.AddHttpContextAccessor(); services.AddSingleton<ITenantProvider>(tenantProvider); services.AddSingleton<ITrialGuard, FakeTrialGuard>();
                    services.AddScoped<AppDbContext>(sp => InMemoryAppDb.Create(sp.GetRequiredService<ITenantProvider>(), database));
                    services.AddScoped<ITenantModuleAccessor, TenantModuleAccessor>(); services.AddScoped<IPermissionResolver, HeaderPermissionResolver>(); services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
                    services.Configure<PlatformAdminOptions>(options => options.Emails = ["platform@test.local"]); services.AddSingleton<IPlatformAdminChecker, PlatformAdminChecker>(); services.AddSingleton<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
                    services.AddAuthorization(options => options.AddRolvixPolicies()); services.AddScoped<Platform.Core.Domain.Contracts.ResourceVisuals.IRentalVisualDefaults, Platform.Api.Modules.Rentals.Services.RentalVisualDefaults>(); services.AddResourceVisuals(); services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
                    services.AddAuthentication(SupabaseJwtBearerDefaults.AuthenticationScheme).AddScheme<AuthenticationSchemeOptions, TestAuthentication>(SupabaseJwtBearerDefaults.AuthenticationScheme, _ => { });
                    services.AddControllers().ConfigureApplicationPartManager(manager =>
                    {
                        manager.ApplicationParts.Clear(); foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToArray()) manager.FeatureProviders.Remove(provider);
                        manager.ApplicationParts.Add(new AssemblyPart(typeof(AdminResourceVisualTemplatesController).Assembly)); manager.FeatureProviders.Add(new CatalogControllers());
                    });
                });
                web.Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); });
            }).StartAsync();
            return new(host, b.Id);
        }
    }
    private sealed class CatalogControllers : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) => typeInfo.Namespace == typeof(AdminResourceVisualTemplatesController).Namespace && typeInfo.Name.EndsWith("Controller", StringComparison.Ordinal);
    }
    private sealed class HeaderPermissionResolver(IHttpContextAccessor accessor) : IPermissionResolver
    {
        private IReadOnlySet<string> Keys => (accessor.HttpContext?.Request.Headers["X-Test-Permissions"].ToString() ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        public Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(Keys);
        public Task<bool> HasPermissionAsync(Guid tenantId, Guid userId, string permissionKey, CancellationToken cancellationToken = default) => Task.FromResult(Keys.Contains(permissionKey));
        public Task<IReadOnlySet<string>> GetAdminWildcardKeysAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult(Keys);
    }
    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Email", out var email)) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", email.ToString()), new Claim("sub", "catalog-test-user"), new Claim(ClaimTypes.Role, "SUPER_ADMIN"), new Claim("permission", ResourceVisualPermissions.TemplatesManage)], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
