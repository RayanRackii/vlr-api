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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Modules.ModuleMenuItems.Controllers;
using Platform.Api.Modules.ModuleMenuItems.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Admin;

public sealed class AdminModuleMenuItemsControllerTests(PostgresContainerFixture postgres)
    : IClassFixture<PostgresContainerFixture>
{
    private const string AdminEmail = "admin@rolvix.test";

    public static TheoryData<int?, string?, string> InvalidSettings => new()
    {
        { 0, null, "BookingIdleTimeoutMinutes" },
        { -1, null, "BookingIdleTimeoutMinutes" },
        { 121, null, "BookingIdleTimeoutMinutes" },
        { int.MaxValue, null, "BookingIdleTimeoutMinutes" },
        { null, "", "BookingIdleRedirectPath" },
        { null, " ", "BookingIdleRedirectPath" },
        { null, "spaces", "BookingIdleRedirectPath" },
        { null, "https://example.com", "BookingIdleRedirectPath" },
        { null, "//example.com", "BookingIdleRedirectPath" },
        { null, "/\\example.com", "BookingIdleRedirectPath" },
        { null, "/\n/example.com", "BookingIdleRedirectPath" },
        { null, "/\t/example.com", "BookingIdleRedirectPath" },
        { null, "/" + new string('a', 200), "BookingIdleRedirectPath" },
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public async Task Create_and_update_invalid_settings_return_400_without_persisting(
        int? timeout, string? path, string field)
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider());
        var tenant = await SeedAsync(db);
        var item = new TenantModuleMenuItem(
            tenant.Id, PlatformModules.Rentals, "Meeting room", 0,
            bookingIdleTimeoutMinutes: 15, bookingIdleRedirectPath: "/spaces");
        db.TenantModuleMenuItems.Add(item);
        await db.SaveChangesAsync();
        using var host = StartHost(db);
        using var client = AdminClient(host);
        var url = AdminUrl(tenant.Id);
        var request = Request(timeout, path);

        using var create = await client.PostAsJsonAsync(url, request);
        await AssertInvalidAsync(create, field);
        using var update = await client.PutAsJsonAsync($"{url}/{item.Id}", request);
        await AssertInvalidAsync(update, field);

        db.ChangeTracker.Clear();
        var persisted = Assert.Single(await db.TenantModuleMenuItems.ToListAsync());
        Assert.Equal("Meeting room", persisted.Label);
        Assert.Equal(15, persisted.BookingIdleTimeoutMinutes);
        Assert.Equal("/spaces", persisted.BookingIdleRedirectPath);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(1, "/")]
    [InlineData(120, "/spaces/meeting-room")]
    [InlineData(15, null)]
    [InlineData(null, "/spaces")]
    public void Domain_accepts_null_and_boundary_settings(int? timeout, string? path)
    {
        var item = new TenantModuleMenuItem(
            Guid.NewGuid(), PlatformModules.Rentals, "Meeting room", 0,
            bookingIdleTimeoutMinutes: timeout, bookingIdleRedirectPath: path);
        Assert.Equal(timeout, item.BookingIdleTimeoutMinutes);
        Assert.Equal(path, item.BookingIdleRedirectPath);

        item.Update("Event space", 1, true, null, timeout, path);
        Assert.Equal(timeout, item.BookingIdleTimeoutMinutes);
        Assert.Equal(path, item.BookingIdleRedirectPath);
    }

    [DockerFact]
    public async Task Admin_create_update_list_and_public_menu_round_trip_settings_in_postgres()
    {
        Assert.NotNull(postgres.Factory);
        await using var db = postgres.Factory.Create(new FakeTenantProvider());
        await AssertRoundTripAsync(db);
    }

    [Fact]
    public async Task Admin_create_update_list_and_public_menu_round_trip_settings_in_memory()
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider());
        await AssertRoundTripAsync(db);
    }

    private static async Task AssertRoundTripAsync(AppDbContext db)
    {
        var tenant = await SeedAsync(db);
        using var host = StartHost(db);
        using var client = AdminClient(host);
        var url = AdminUrl(tenant.Id);
        var path = "/" + new string('a', 199);

        using var create = await client.PostAsJsonAsync(url, Request(1, path));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        AssertSettings(created, 1, path);
        var id = created.GetProperty("id").GetGuid();
        db.ChangeTracker.Clear();
        var persisted = await db.TenantModuleMenuItems.SingleAsync(i => i.Id == id);
        Assert.Equal(1, persisted.BookingIdleTimeoutMinutes);
        Assert.Equal(path, persisted.BookingIdleRedirectPath);
        await AssertReadSettingsAsync(host, tenant, id, 1, path);

        using var update = await client.PutAsJsonAsync($"{url}/{id}", Request(120, "/spaces"));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        AssertSettings(await update.Content.ReadFromJsonAsync<JsonElement>(), 120, "/spaces");
        db.ChangeTracker.Clear();
        await AssertReadSettingsAsync(host, tenant, id, 120, "/spaces");

        // Explicit null resets both values to the portal defaults.
        using var clear = await client.PutAsJsonAsync($"{url}/{id}", Request(null, null));
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        AssertSettings(await clear.Content.ReadFromJsonAsync<JsonElement>(), null, null);
        db.ChangeTracker.Clear();
        await AssertReadSettingsAsync(host, tenant, id, null, null);
    }

    [DockerFact]
    public async Task Legacy_payloads_default_to_null_and_settings_remain_per_menu_item()
    {
        Assert.NotNull(postgres.Factory);
        await using var db = postgres.Factory.Create(new FakeTenantProvider());
        var tenant = await SeedAsync(db);
        using var host = StartHost(db);
        using var client = AdminClient(host);
        var url = AdminUrl(tenant.Id);
        var legacy = new { moduleName = PlatformModules.Rentals, label = "Event space", isActive = true };

        using var create = await client.PostAsJsonAsync(url, legacy);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();
        AssertSettings(created, null, null);

        using var configured = await client.PostAsJsonAsync(url, Request(20, "/spaces"));
        Assert.Equal(HttpStatusCode.Created, configured.StatusCode);
        var configuredId = (await configured.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        db.ChangeTracker.Clear();
        await AssertReadSettingsAsync(host, tenant, id, null, null);
        await AssertReadSettingsAsync(host, tenant, configuredId, 20, "/spaces");

        // PUT follows the existing replacement semantics: omitted optional fields reset to null.
        using var update = await client.PutAsJsonAsync($"{url}/{configuredId}", legacy);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        db.ChangeTracker.Clear();
        await AssertReadSettingsAsync(host, tenant, configuredId, null, null);
    }

    private static object Request(int? timeout, string? path) => new
    {
        moduleName = PlatformModules.Rentals,
        label = "Event space",
        sortOrder = 1,
        isActive = true,
        bookingIdleTimeoutMinutes = timeout,
        bookingIdleRedirectPath = path,
    };

    private static async Task AssertInvalidAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(field, body.GetProperty("error").GetString());
    }

    private static void AssertSettings(JsonElement item, int? timeout, string? path)
    {
        var timeoutValue = item.GetProperty("bookingIdleTimeoutMinutes");
        var pathValue = item.GetProperty("bookingIdleRedirectPath");
        if (timeout.HasValue)
            Assert.Equal(timeout.Value, timeoutValue.GetInt32());
        else
            Assert.Equal(JsonValueKind.Null, timeoutValue.ValueKind);
        Assert.Equal(path, pathValue.GetString());
    }

    private static async Task AssertReadSettingsAsync(
        IHost host, Tenant tenant, Guid id, int? timeout, string? path)
    {
        using var admin = AdminClient(host);
        using var adminResponse = await admin.GetAsync(AdminUrl(tenant.Id));
        Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
        var adminItems = await adminResponse.Content.ReadFromJsonAsync<JsonElement>();
        AssertSettings(adminItems.EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == id), timeout, path);

        // Public portal reads have no authentication header.
        using var portal = host.GetTestClient();
        using var publicResponse = await portal.GetAsync($"/api/public/tenants/{tenant.Subdomain}/menu");
        Assert.Equal(HttpStatusCode.OK, publicResponse.StatusCode);
        var publicItems = await publicResponse.Content.ReadFromJsonAsync<JsonElement>();
        AssertSettings(publicItems.EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == id), timeout, path);
    }

    private static string AdminUrl(Guid tenantId) => $"/api/admin/tenants/{tenantId}/module-menu-items";

    private static HttpClient AdminClient(IHost host)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Email", AdminEmail);
        return client;
    }

    private static async Task<Tenant> SeedAsync(AppDbContext db)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = new Tenant("Space rentals", suffix[..20], subdomain: $"spaces-{suffix}");
        db.Tenants.Add(tenant);
        db.TenantModules.Add(new TenantModule(tenant.Id, PlatformModules.Rentals));
        await db.SaveChangesAsync();
        return tenant;
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
                services.AddScoped<IModuleMenuItemService, ModuleMenuItemService>();
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
                    manager.ApplicationParts.Add(new AssemblyPart(typeof(AdminModuleMenuItemsController).Assembly));
                    manager.FeatureProviders.Add(new MenuControllerFeatureProvider());
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

    private sealed class MenuControllerFeatureProvider : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) =>
            typeInfo.AsType() == typeof(AdminModuleMenuItemsController)
            || typeInfo.AsType() == typeof(ModuleMenuItemsController);
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
