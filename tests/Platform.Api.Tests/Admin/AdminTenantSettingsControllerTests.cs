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
using Platform.Api.Modules.Admin.Controllers;
using Platform.Api.Modules.Admin.Services;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Services.Tenancy;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Rentals;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Admin;

public sealed class AdminTenantSettingsControllerTests
{
    private const string AdminEmail = "admin@rolvix.test";

    private static string Url(Guid tenantId, string key) => $"/api/admin/tenants/{tenantId}/settings/{key}";

    private static async Task<List<TenantSetting>> RowsAsync(AppDbContext db, Guid tenantId, string key)
    {
        db.ChangeTracker.Clear();
        return await TenantSettingStore.Query(db, tenantId, key).AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task Get_returns_a_null_value_until_the_setting_exists()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        using var host = StartHost(h.Db);
        using var client = AdminClient(host);

        using var response = await client.GetAsync(Url(h.Tenant.Id, TenantSettingKeys.BookingUiEnabled));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(TenantSettingKeys.BookingUiEnabled, body.GetProperty("key").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task Only_platform_admins_can_read_or_write_settings()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        using var host = StartHost(h.Db);
        var url = Url(h.Tenant.Id, TenantSettingKeys.BookingUiEnabled);

        using var anonymous = host.GetTestClient();
        using var anonymousGet = await anonymous.GetAsync(url);
        using var anonymousPut = await anonymous.PutAsJsonAsync(url, new { value = "true" });
        Assert.Contains(anonymousGet.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        Assert.Contains(anonymousPut.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });

        using var tenantUser = host.GetTestClient();
        tenantUser.DefaultRequestHeaders.Add("X-Test-Email", "someone@club.test");
        using var userGet = await tenantUser.GetAsync(url);
        using var userPut = await tenantUser.PutAsJsonAsync(url, new { value = "true" });
        Assert.Equal(HttpStatusCode.Forbidden, userGet.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, userPut.StatusCode);

        Assert.Empty(await RowsAsync(h.Db, h.Tenant.Id, TenantSettingKeys.BookingUiEnabled));
    }

    [Theory]
    [InlineData("booking.timeZoneId")]
    [InlineData("rentals.other")]
    [InlineData("Rentals.Booking_Ui.Enabled")]
    public async Task Keys_outside_the_allow_list_are_not_found_and_never_written(string key)
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        using var host = StartHost(h.Db);
        using var client = AdminClient(host);

        using var get = await client.GetAsync(Url(h.Tenant.Id, key));
        using var put = await client.PutAsJsonAsync(Url(h.Tenant.Id, key), new { value = "America/Manaus" });

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Empty(await RowsAsync(h.Db, h.Tenant.Id, key));
    }

    [Fact]
    public async Task An_unknown_tenant_is_not_found()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        using var host = StartHost(h.Db);
        using var client = AdminClient(host);
        var unknown = Guid.NewGuid();

        using var get = await client.GetAsync(Url(unknown, TenantSettingKeys.BookingUiEnabled));
        using var put = await client.PutAsJsonAsync(Url(unknown, TenantSettingKeys.BookingUiEnabled), new { value = "true" });

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Empty(await RowsAsync(h.Db, unknown, TenantSettingKeys.BookingUiEnabled));
    }

    [Fact]
    public async Task Booking_ui_setting_accepts_only_true_or_false_and_is_stored_canonically()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        using var host = StartHost(h.Db);
        using var client = AdminClient(host);
        var url = Url(h.Tenant.Id, TenantSettingKeys.BookingUiEnabled);

        foreach (var bad in new[] { "", "yes", "1", "0", "enabled", "null" })
        {
            using var rejected = await client.PutAsJsonAsync(url, new { value = bad });
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }

        using var missing = await client.PutAsJsonAsync(url, new { });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Empty(await RowsAsync(h.Db, h.Tenant.Id, TenantSettingKeys.BookingUiEnabled));

        using var enabled = await client.PutAsJsonAsync(url, new { value = " TRUE " });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.Equal("true", (await enabled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("value").GetString());
        Assert.Equal("true", Assert.Single(await RowsAsync(h.Db, h.Tenant.Id, TenantSettingKeys.BookingUiEnabled)).Value);
        Assert.True(await TenantSettingStore.IsBookingUiEnabledAsync(h.Db, h.Tenant.Id, default));

        using var disabled = await client.PutAsJsonAsync(url, new { value = "false" });
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.Equal("false", Assert.Single(await RowsAsync(h.Db, h.Tenant.Id, TenantSettingKeys.BookingUiEnabled)).Value);
        Assert.False(await TenantSettingStore.IsBookingUiEnabledAsync(h.Db, h.Tenant.Id, default));

        using var read = await client.GetAsync(url);
        Assert.Equal("false", (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("value").GetString());
    }

    [Fact]
    public async Task Teacher_lesson_setting_accepts_only_rental_assets_of_that_tenant()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        var other = new Tenant("Hidden tenant", "22222222000191", subdomain: "hidden");
        var otherUnit = new Unit(other.Id, "Hidden unit");
        h.Db.AddRange(other, otherUnit);
        var foreignAsset = await h.AddResourceAsync(other, otherUnit, "Hidden resource");
        using var host = StartHost(h.Db);
        using var client = AdminClient(host);
        var url = Url(h.Tenant.Id, TenantSettingKeys.TeacherLessonRentalAssetIds);
        var own = h.Primary.Id;

        foreach (var bad in new[]
        {
            "", "not json", "{}", "\"x\"", "[1]", "[\"not-a-guid\"]",
            $"[\"{Guid.Empty}\"]",
            $"[\"{own}\", \"{Guid.NewGuid()}\"]",
            $"[\"{foreignAsset.Id}\"]",
        })
        {
            using var rejected = await client.PutAsJsonAsync(url, new { value = bad });
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }

        Assert.Empty(await RowsAsync(h.Db, h.Tenant.Id, TenantSettingKeys.TeacherLessonRentalAssetIds));

        using var accepted = await client.PutAsJsonAsync(url, new { value = $"[ \"{own.ToString().ToUpperInvariant()}\", \"{own}\" ]" });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var stored = Assert.Single(await RowsAsync(h.Db, h.Tenant.Id, TenantSettingKeys.TeacherLessonRentalAssetIds));
        Assert.Equal(TenantSettingValues.FormatRentalAssetIds([own]), stored.Value);

        // The scope the lesson endpoints use sees the new setting.
        var scope = new TenantSettingTeacherLessonScope(h.Db);
        Assert.True(await scope.AllowsAsync(h.Tenant.Id, own, default));
        Assert.False(await scope.AllowsAsync(h.Tenant.Id, foreignAsset.Id, default));

        // An empty list is valid and turns lessons off again; the row is updated, not duplicated.
        using var cleared = await client.PutAsJsonAsync(url, new { value = "[]" });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal("[]", Assert.Single(await RowsAsync(h.Db, h.Tenant.Id, TenantSettingKeys.TeacherLessonRentalAssetIds)).Value);
        Assert.False(await new TenantSettingTeacherLessonScope(h.Db).AllowsAsync(h.Tenant.Id, own, default));

        // Nothing leaked to the other tenant.
        Assert.Empty(await RowsAsync(h.Db, other.Id, TenantSettingKeys.TeacherLessonRentalAssetIds));
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
                services.AddScoped<ITenantSettingsAdminService, TenantSettingsAdminService>();
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
                    manager.ApplicationParts.Add(new AssemblyPart(typeof(AdminTenantSettingsController).Assembly));
                    manager.FeatureProviders.Add(new SettingsControllerFeatureProvider());
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

    private sealed class SettingsControllerFeatureProvider : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) =>
            typeInfo.AsType() == typeof(AdminTenantSettingsController);
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
