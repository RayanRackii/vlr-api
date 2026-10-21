using Platform.Api.Modules.Admin.Controllers;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Modules.Rentals;
using Platform.Api.Modules.Rentals.Controllers;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Modules.ResourceVisuals;
using Platform.Api.Services.Trial;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Rentals;

public sealed class BookingMapHttpTests
{
    private const string PublicPath = "/api/public/tenants/example/booking-maps";

    [Fact]
    public async Task New_public_maps_are_flag_gated_while_legacy_route_keeps_compatibility()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        await BookingMapTests.Service(h).CreateAsync(BookingMapTests.Legacy(h), default);
        using var host = StartHost(h, enabled: false);
        var client = host.GetTestClient();
        var response = await client.GetAsync(PublicPath);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("BOOKING_MAPS_DISABLED", await Code(response));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/public/tenants/example/rental-layouts")).StatusCode);
    }

    [Fact]
    public async Task Public_route_tenant_overrides_hostile_header_and_etag_tracks_resource_activation()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        var service = BookingMapTests.Service(h);
        var map = await service.CreateAsync(BookingMapTests.Legacy(h), default);
        var other = new Tenant("Hidden tenant", "22222222000191", subdomain: "hidden");
        var unit = new Unit(other.Id, "Hidden unit"); h.Db.AddRange(other, unit, new TenantModule(other.Id, PlatformModules.Rentals));
        await h.AddResourceAsync(other, unit, "Hidden resource");
        using var host = StartHost(h, additionalEnabledTenant: other.Id);
        var client = host.GetTestClient(); client.DefaultRequestHeaders.Add("X-Tenant-Subdomain", "hidden");
        var response = await client.GetAsync($"{PublicPath}/{map.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(h.Primary.Id, body.RootElement.GetProperty("items")[0].GetProperty("rentalAssetId").GetGuid());
        foreach (var field in new[] { "tenantId", "isActive", "requiresDeposit", "customer", "reservationId", "notes" })
            Assert.DoesNotContain($"\"{field}\"", body.RootElement.GetRawText());
        var tag = response.Headers.ETag!.ToString();
        client.DefaultRequestHeaders.IfNoneMatch.ParseAdd(tag);
        Assert.Equal(HttpStatusCode.NotModified, (await client.GetAsync($"{PublicPath}/{map.Id}")).StatusCode);
        h.Primary.IsActive = false; await h.Db.SaveChangesAsync();
        var changed = await client.GetAsync($"{PublicPath}/{map.Id}");
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode); Assert.NotEqual(tag, changed.Headers.ETag!.ToString());
        using var empty = JsonDocument.Parse(await changed.Content.ReadAsStringAsync());
        Assert.Empty(empty.RootElement.GetProperty("items").EnumerateArray());
        Assert.DoesNotContain(h.Primary.Asset.Name, empty.RootElement.GetRawText());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/public/tenants/hidden/booking-maps/{map.Id}")).StatusCode);
    }

    [Fact]
    public async Task Composition_http_honors_version_and_rejects_hostile_decoration_atomically()
    {
        await using var h = await AvailabilityHarness.CreateAsync(); var service = BookingMapTests.Service(h);
        var map = await service.CreateAsync(BookingMapTests.Legacy(h), default);
        var initial = (await service.GetCompositionAsync(map.Id, default))!;
        using var host = StartHost(h);
        var client = host.GetTestClient(); Authenticate(client, Permissions.Rentals.LayoutsWrite, Permissions.Rentals.LayoutsRead);
        var path = $"/api/rental-layouts/{map.Id}/composition";
        var save = BookingMapTests.Composition(initial) with { Decorations = [BookingMapTests.Decoration()] };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(path, save)).StatusCode);
        var conflict = await client.PutAsJsonAsync(path, save);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode); Assert.Equal("MAP_VERSION_CONFLICT", await Code(conflict));
        var bad = save with { ExpectedVersion = 2, Name = "Must not change", Decorations = [BookingMapTests.Decoration("{\"fill\":\"url(javascript:evil)\"}")] };
        var invalid = await client.PutAsJsonAsync(path, bad);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); Assert.Equal("MAP_INVALID", await Code(invalid));
        using var unchanged = JsonDocument.Parse(await (await client.GetAsync(path)).Content.ReadAsStringAsync());
        Assert.Equal(2, unchanged.RootElement.GetProperty("version").GetInt64());
        Assert.Equal(initial.Name, unchanged.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Missing_permissions_cannot_save_composition_settings_or_seed_demo()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        using var host = StartHost(h);
        var client = host.GetTestClient(); Authenticate(client, Permissions.Rentals.LayoutsRead);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/rental-layouts/{Guid.NewGuid()}/composition", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/rentals/booking-map-settings", BookingMapTests.Settings(0))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/admin/tenants/{h.Tenant.Id}/booking-maps/demo", new { })).StatusCode);
        // A tenant super-admin permission string does not satisfy either platform policy.
        Authenticate(client, "SUPER_ADMIN", ResourceVisualPermissions.DemoMapsSeed);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/admin/tenants/{h.Tenant.Id}/booking-maps/demo", new { })).StatusCode);
    }

    [Fact]
    public async Task Demo_endpoint_is_dev_only_and_validates_explicit_tenant_manifest()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        var request = new SeedDemoBookingMapRequest { UnitId = h.Unit.Id,
            Items = [new() { RentalAssetId = h.Primary.Id, XPercent = 0, YPercent = 0, WidthPercent = 30, HeightPercent = 30 }] };
        using (var host = StartHost(h, environment: Environments.Production))
        {
            var client = host.GetTestClient(); client.DefaultRequestHeaders.Add("X-Map-Admin", "true");
            var blocked = await client.PostAsJsonAsync($"/api/admin/tenants/{h.Tenant.Id}/booking-maps/demo", request);
            Assert.Equal(HttpStatusCode.NotFound, blocked.StatusCode); Assert.Equal("DEV_ONLY", await Code(blocked));
        }
        using (var host = StartHost(h))
        {
            var client = host.GetTestClient(); client.DefaultRequestHeaders.Add("X-Map-Admin", "true");
            var seeded = await client.PostAsJsonAsync($"/api/admin/tenants/{h.Tenant.Id}/booking-maps/demo", request);
            Assert.Equal(HttpStatusCode.OK, seeded.StatusCode);
            var foreign = new Tenant("Other", "22222222000191", subdomain: "other"); h.Db.Add(foreign); await h.Db.SaveChangesAsync();
            var rejected = await client.PostAsJsonAsync($"/api/admin/tenants/{foreign.Id}/booking-maps/demo", request);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode); Assert.Equal("MAP_INVALID", await Code(rejected));
        }
    }

    [Fact]
    public async Task Malformed_new_map_requests_have_stable_errors()
    {
        await using var h = await AvailabilityHarness.CreateAsync(); using var host = StartHost(h);
        var client = host.GetTestClient(); Authenticate(client, Permissions.Rentals.LayoutsWrite);
        var invalid = await client.PutAsJsonAsync($"/api/rental-layouts/{Guid.NewGuid()}/composition", new { expectedVersion = "bad" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); Assert.Equal("MAP_INVALID", await Code(invalid));
        var unit = await client.GetAsync(PublicPath + "?unitId=bad");
        Assert.Equal(HttpStatusCode.BadRequest, unit.StatusCode); Assert.Equal("MAP_INVALID", await Code(unit));
    }

    private static async Task<string?> Code(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString();
    }
    private static void Authenticate(HttpClient client, params string[] permissions)
    {
        client.DefaultRequestHeaders.Remove("X-Map-Permissions");
        client.DefaultRequestHeaders.Add("X-Map-Permissions", string.Join(',', permissions));
    }
    private static IHost StartHost(AvailabilityHarness h, bool enabled = true, string environment = "Development", Guid? additionalEnabledTenant = null) =>
        new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseEnvironment(environment).UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddLogging(); services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
                services.AddScoped<AmbientTenantContext>();
                services.AddScoped<ITenantProvider>(sp => new MapTenantProvider(sp.GetRequiredService<AmbientTenantContext>(), h.Tenant.Id));
                services.AddScoped(sp => InMemoryAppDb.Create(sp.GetRequiredService<ITenantProvider>(), h.DatabaseName));
                services.AddScoped<IPublicTenantBinder, PublicTenantBinder>();
                services.AddScoped<ITenantModuleAccessor, TenantModuleAccessor>();
                services.AddScoped<ITrialGuard, FakeTrialGuard>();
                services.AddScoped<IRentalLayoutService, RentalLayoutService>();
                services.Configure<BookingUiOptions>(o => o.EnabledTenantIds = enabled
                    ? additionalEnabledTenant is { } extra ? [h.Tenant.Id, extra] : [h.Tenant.Id] : []);
                services.AddAuthentication("MapTest").AddScheme<AuthenticationSchemeOptions, MapAuthHandler>("MapTest", _ => { });
                services.AddAuthorization(o =>
                {
                    foreach (var key in new[] { Permissions.Rentals.LayoutsRead, Permissions.Rentals.LayoutsWrite,
                        Permissions.Rentals.MapSettingsRead, Permissions.Rentals.MapSettingsWrite })
                        o.AddPolicy(PermissionPolicies.Name(key), p => p.RequireAuthenticatedUser().RequireClaim("perm", key));
                    foreach (var key in new[] { SupabaseAuthenticationExtensions.PlatformAdminPolicy, ResourceVisualPermissions.DemoMapsSeed })
                        o.AddPolicy(key, p => p.RequireAuthenticatedUser().RequireClaim("platform", "true"));
                });
                services.AddControllers().ConfigureApplicationPartManager(m =>
                {
                    m.ApplicationParts.Clear();
                    foreach (var provider in m.FeatureProviders.OfType<ControllerFeatureProvider>().ToArray()) m.FeatureProviders.Remove(provider);
                    m.ApplicationParts.Add(new AssemblyPart(typeof(BookingMapsController).Assembly));
                    m.FeatureProviders.Add(new MapControllers());
                });
            });
            web.Configure(app =>
            {
                app.UsePlatformExceptionHandler(); app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
                app.UseEndpoints(e => e.MapControllers());
            });
        }).Start();
    private sealed class MapControllers : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo t) => new[] { typeof(BookingMapsController), typeof(PublicBookingMapsController),
            typeof(AdminBookingMapDemoController), typeof(RentalLayoutsController) }.Contains(t.AsType());
    }
    private sealed class MapTenantProvider(AmbientTenantContext ambient, Guid tenantId) : ITenantProvider
    { public Guid? TenantId => ambient.TenantId ?? tenantId; }
    private sealed class MapAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var permissions = Request.Headers["X-Map-Permissions"].ToString();
            var platform = Request.Headers["X-Map-Admin"].ToString() == "true";
            if (permissions.Length == 0 && !platform) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = permissions.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => new Claim("perm", p)).ToList();
            if (platform) claims.Add(new("platform", "true"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }
}
