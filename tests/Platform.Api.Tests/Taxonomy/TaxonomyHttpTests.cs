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
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Modules.Catalog.Controllers;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Modules.Taxonomy;
using Platform.Api.Modules.Taxonomy.Controllers;
using Platform.Api.Tests.Catalog;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Taxonomy;

public sealed class TaxonomyHttpTests
{
    [Theory]
    [InlineData("/api/taxonomy/categories", "core.taxonomy.read", false, false, 200)]
    [InlineData("/api/taxonomy/tags", "core.taxonomy.manage", false, false, 403)]
    [InlineData("/api/taxonomy/tags", "core.taxonomy.read", false, false, 200)]
    [InlineData("/api/taxonomy/categories", "catalog.products.read", true, false, 403)]
    [InlineData("/api/taxonomy/tags", "core.taxonomy.read", true, true, 403)]
    [InlineData("/api/catalog/taxonomy-options", "catalog.products.read", true, false, 200)]
    [InlineData("/api/catalog/taxonomy-options", "catalog.products.read", false, false, 403)]
    [InlineData("/api/catalog/taxonomy-options", "core.taxonomy.read", true, false, 403)]
    [InlineData("/api/catalog/taxonomy-options", "catalog.products.read", true, true, 403)]
    [InlineData("/api/catalog/portal/taxonomy-options", "", true, true, 200)]
    [InlineData("/api/catalog/portal/taxonomy-options", "", false, true, 403)]
    [InlineData("/api/catalog/portal/taxonomy-options", "catalog.products.read", true, false, 401)]
    public async Task Permission_and_module_matrix(string path, string permission, bool catalog, bool customer, int status)
    {
        await using var h = await CatalogHarness.CreateAsync(catalog);
        using var host = StartHost(h);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Permission", permission);
        if (customer) client.DefaultRequestHeaders.Add("X-Customer", "true");
        Assert.Equal(status, (int)(await client.GetAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData("core.taxonomy.read", false, 403)]
    [InlineData("core.taxonomy.manage", false, 201)]
    [InlineData("core.taxonomy.manage", true, 403)]
    public async Task Mutations_require_manage_and_reject_customers(string permission, bool customer, int expected)
    {
        await using var h = await CatalogHarness.CreateAsync(false);
        using var host = StartHost(h);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Permission", permission);
        if (customer) client.DefaultRequestHeaders.Add("X-Customer", "true");
        var response = await client.PostAsJsonAsync("/api/taxonomy/tags", new { name = "Tag", slug = "tag" });
        Assert.Equal(expected, (int)response.StatusCode);
    }

    [Fact]
    public async Task Crud_returns_locations_status_codes_and_standard_error_envelopes()
    {
        await using var h = await CatalogHarness.CreateAsync(false);
        using var host = StartHost(h);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Permission", "core.taxonomy.read,core.taxonomy.manage");
        var created = await client.PostAsJsonAsync("/api/taxonomy/categories", new { name = "Root", slug = "root", icon = "folder", sortOrder = 0 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.EndsWith($"/api/taxonomy/categories/{id}", created.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(created.Headers.Location)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/taxonomy/categories", new { name = "Duplicate", slug = "root" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/taxonomy/categories/{id}", new { name = "Renamed", parentId = (Guid?)null, icon = (string?)null, sortOrder = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/taxonomy/categories/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/taxonomy/categories/{id}")).StatusCode);
        var tag = await client.PostAsJsonAsync("/api/taxonomy/tags", new { name = "Tag", slug = "tag" });
        Assert.Equal(HttpStatusCode.Created, tag.StatusCode);
        var location = tag.Headers.Location!;
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(location, new { name = "Renamed" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(location)).StatusCode);
    }

    [Theory]
    [InlineData("/api/taxonomy/categories", "{}")]
    [InlineData("/api/taxonomy/categories", "{\"name\":null,\"slug\":\"x\"}")]
    [InlineData("/api/taxonomy/categories", "{\"name\":\"X\",\"slug\":\"x\",\"parentId\":\"bad\"}")]
    [InlineData("/api/taxonomy/tags", "{\"name\":\"X\"}")]
    [InlineData("/api/catalog/products", "{\"name\":\"X\",\"taxonomy\":{\"categoryIds\":[\"bad\"],\"tagIds\":[]}}")]
    [InlineData("/api/catalog/products", "{\"name\":\"X\",\"taxonomy\":{}}")]
    public async Task Invalid_body_retains_error_and_code(string path, string json)
    {
        await using var h = await CatalogHarness.CreateAsync();
        using var host = StartHost(h);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Permission", "core.taxonomy.manage,catalog.products.manage");
        var response = await client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("TAXONOMY_INVALID", body.GetProperty("code").GetString());
        Assert.True(body.TryGetProperty("error", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Malformed_filter_returns_400_and_old_list_response_remains_an_array(bool portal)
    {
        await using var h = await CatalogHarness.CreateAsync();
        using var host = StartHost(h);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Permission", "catalog.products.read");
        if (portal) client.DefaultRequestHeaders.Add("X-Customer", "true");
        var path = portal ? "/api/catalog/portal/products" : "/api/catalog/products";
        var good = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        Assert.Equal(JsonValueKind.Array, (await good.Content.ReadFromJsonAsync<JsonElement>()).ValueKind);
        var bad = await client.GetAsync(path + "?tags=bad");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("TAXONOMY_INVALID", (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Bootstrap_and_resolver_grant_admin_and_explicit_custom_roles_without_default_user_or_technician_grants()
    {
        await using var h = await CatalogHarness.CreateAsync(false);
        h.Db.Roles.Add(new Role(h.Tenant.Id, SystemRoles.Technician, isSystemRole: true));
        await h.Db.SaveChangesAsync();
        await new TenantAccessBootstrapper(h.Db).EnsureAsync(h.Tenant.Id);
        var permissions = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(h.Db.Permissions);
        var keys = new[] { Permissions.Core.TaxonomyRead, Permissions.Core.TaxonomyManage };
        Assert.All(permissions.Where(x => keys.Contains(x.Key)), p => Assert.Null(p.ModuleKey));
        var custom = new Role(h.Tenant.Id, "Custom");
        h.Db.Roles.Add(custom);
        foreach (var p in permissions.Where(x => keys.Contains(x.Key))) h.Db.RolePermissions.Add(new(custom.Id, p.Id));
        await h.Db.SaveChangesAsync();
        var roles = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(h.Db.Roles);
        foreach (var role in roles)
        {
            var user = new User(h.Tenant.Id, Guid.NewGuid().ToString(), role.Name, Guid.NewGuid() + "@test.local");
            h.Db.Users.Add(user);
            h.Db.UserRoles.Add(new UserRole(user.Id, role.Id));
            await h.Db.SaveChangesAsync();
            var resolver = new PermissionResolver(h.Db, new TenantModuleAccessor(h.Db, h.TenantProvider), h.TenantProvider, NullLogger<PermissionResolver>.Instance);
            foreach (var key in keys)
                Assert.Equal(role.Name is SystemRoles.Admin or "Custom", await resolver.HasPermissionAsync(h.Tenant.Id, user.Id, key));
        }
    }

    private static IHost StartHost(CatalogHarness h) => new HostBuilder().ConfigureWebHost(web =>
    {
        web.UseTestServer();
        web.ConfigureServices(services =>
        {
            services.AddLogging();
            services.AddSingleton(h.Db);
            services.AddSingleton<ITenantProvider>(h.TenantProvider);
            services.AddSingleton<ITenantModuleAccessor, TenantModuleAccessor>();
            services.AddTaxonomyModule();
            services.AddScoped<CatalogTaxonomyOptionsService>();
            services.AddSingleton<ICatalogProductService>(h.Products);
            services.AddSingleton<ICatalogPortalService>(h.Portal);
            services.AddSingleton<IAuthorizationHandler, ClaimPermissionHandler>();
            services.AddAuthentication(SupabaseJwtBearerDefaults.AuthenticationScheme)
                .AddScheme<AuthenticationSchemeOptions, TestAuth>(SupabaseJwtBearerDefaults.AuthenticationScheme, _ => { })
                .AddScheme<AuthenticationSchemeOptions, TestAuth>(CustomerJwtBearerDefaults.AuthenticationScheme, _ => { });
            services.AddAuthorization(o => o.AddRolvixPolicies());
            services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
            services.AddControllers().ConfigureApplicationPartManager(manager =>
            {
                manager.ApplicationParts.Clear();
                manager.ApplicationParts.Add(new AssemblyPart(typeof(TaxonomyController).Assembly));
                foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToArray()) manager.FeatureProviders.Remove(provider);
                manager.FeatureProviders.Add(new Controllers());
            });
        });
        web.Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(e => e.MapControllers()); });
    }).Start();

    private sealed class Controllers : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) => new[] { typeof(TaxonomyController), typeof(CatalogTaxonomyController), typeof(CatalogProductsController), typeof(CatalogPortalController) }.Contains(typeInfo.AsType());
    }
    private sealed class ClaimPermissionHandler : AuthorizationHandler<PermissionRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
        {
            if (context.User.HasClaim("permission", requirement.PermissionKey)) context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }
    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var customer = Request.Headers.ContainsKey("X-Customer");
            if (Scheme.Name == CustomerJwtBearerDefaults.AuthenticationScheme && !customer) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = Request.Headers["X-Permission"].ToString().Split(',').Select(x => new Claim("permission", x)).ToList();
            if (customer) claims.Add(new Claim(ClaimTypes.Role, AuthRoles.Customer));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
