using System.Net;
using System.Reflection;
using System.Security.Claims;
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
using Platform.Api.Authorization;
using Platform.Api.Modules.Rentals.Controllers;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Rentals;

public sealed class RentalsReadinessHttpTests
{
    [Fact]
    public async Task Empty_tenant_returns_not_ready_and_the_default_policy_item()
    {
        await using var h = await Harness.CreateAsync();

        var response = await h.GetAsync(h.EmptyTenantId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("ready").GetBoolean());
        var policy = Item(body, "hasBookingPolicy");
        Assert.True(policy.GetProperty("done").GetBoolean());
        Assert.False(policy.TryGetProperty("count", out _));
    }

    [Fact]
    public async Task Configured_tenant_returns_ready()
    {
        await using var h = await Harness.CreateAsync();
        await h.ConfigureAsync(h.ConfiguredTenantId);

        var response = await h.GetAsync(h.ConfiguredTenantId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("ready").GetBoolean());
        Assert.True(Item(body, "hasUnits").GetProperty("done").GetBoolean());
        Assert.True(Item(body, "hasRentableAssets").GetProperty("done").GetBoolean());
        Assert.True(Item(body, "hasScheduleTemplateOrRule").GetProperty("done").GetBoolean());
    }

    [Fact]
    public async Task Tenant_cannot_read_another_tenants_readiness_data()
    {
        await using var h = await Harness.CreateAsync();
        await h.ConfigureAsync(h.ConfiguredTenantId);

        var response = await h.GetAsync(h.EmptyTenantId);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("ready").GetBoolean());
        Assert.Equal(0, Item(body, "hasRentableAssets").GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Missing_tenant_context_is_denied_by_the_module_gate()
    {
        await using var h = await Harness.CreateAsync();

        var response = await h.GetAsync(null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(RequireActiveModuleAttribute.InactiveModuleError, body.RootElement.GetProperty("error").GetString());
        Assert.Equal(RequireActiveModuleAttribute.InactiveModuleCode, body.RootElement.GetProperty("code").GetString());
        Assert.Equal(PlatformModules.Rentals, body.RootElement.GetProperty("module").GetString());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Inactive_module_or_missing_read_permission_is_forbidden(bool active, bool canRead)
    {
        await using var h = await Harness.CreateAsync();

        var response = await h.GetAsync(h.EmptyTenantId, active, canRead);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static JsonElement Item(JsonDocument body, string key) =>
        body.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("key").GetString() == key);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _databaseName = $"rentals-readiness-{Guid.NewGuid():N}";
        public Guid EmptyTenantId { get; } = Guid.NewGuid();
        public Guid ConfiguredTenantId { get; } = Guid.NewGuid();

        public static async Task<Harness> CreateAsync()
        {
            var harness = new Harness();
            await using var db = InMemoryAppDb.Create(new FixedTenantProvider(null), harness._databaseName);
            db.AddRange(
                new Tenant("Empty", "11111111000111"),
                new Tenant("Configured", "22222222000122"));
            await db.SaveChangesAsync();
            return harness;
        }

        public async Task ConfigureAsync(Guid tenantId)
        {
            await using var db = InMemoryAppDb.Create(new FixedTenantProvider(null), _databaseName);
            var unit = new Unit(tenantId, "Main");
            var rental = new RentalAsset
            {
                TenantId = tenantId,
                AssetId = Guid.NewGuid(),
                Type = RentalAssetType.Location,
                TotalQuantity = 1,
                IsActive = true,
                RequiresDeposit = false,
                SchedulePolicy = SchedulePolicy.SlotGrid,
            };
            db.AddRange(unit, rental, new ScheduleTemplate
            {
                TenantId = tenantId,
                RentalAssetId = rental.Id,
                DayOfWeek = DayOfWeek.Monday,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(9, 0),
                OccupancyKindId = Guid.NewGuid(),
                IsActive = true,
            });
            await db.SaveChangesAsync();
        }

        public async Task<HttpResponseMessage> GetAsync(Guid? tenantId, bool active = true, bool canRead = true)
        {
            using var host = StartHost();
            var client = host.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Readiness-Auth", "true");
            if (tenantId is not null) client.DefaultRequestHeaders.Add("X-Readiness-Tenant", tenantId.Value.ToString());
            if (active) client.DefaultRequestHeaders.Add("X-Readiness-Rentals", "true");
            if (canRead) client.DefaultRequestHeaders.Add("X-Readiness-Permission", Permissions.Rentals.AssetsRead);
            return await client.GetAsync("/api/rentals/readiness");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private IHost StartHost() => new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddHttpContextAccessor();
                services.AddScoped<ITenantProvider, HeaderTenantProvider>();
                services.AddScoped(sp => InMemoryAppDb.Create(
                    sp.GetRequiredService<ITenantProvider>(), _databaseName));
                services.AddScoped<IRentalsReadinessService, RentalsReadinessService>();
                services.AddScoped<ITenantModuleAccessor, HeaderModuleAccessor>();
                services.AddSingleton<IAuthorizationHandler, HeaderPermissionHandler>();
                services.AddAuthentication("ReadinessTest")
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("ReadinessTest", _ => { });
                services.AddAuthorization();
                services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
                services.AddControllers().ConfigureApplicationPartManager(manager =>
                {
                    manager.ApplicationParts.Clear();
                    foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToArray())
                        manager.FeatureProviders.Remove(provider);
                    manager.ApplicationParts.Add(new AssemblyPart(typeof(RentalsReadinessController).Assembly));
                    manager.FeatureProviders.Add(new ReadinessControllerProvider());
                });
            });
            web.Configure(app =>
            {
                app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            });
        }).Start();
    }

    private sealed class FixedTenantProvider(Guid? tenantId) : ITenantProvider { public Guid? TenantId => tenantId; }

    private sealed class HeaderTenantProvider(IHttpContextAccessor context) : ITenantProvider
    {
        public Guid? TenantId => Guid.TryParse(context.HttpContext?.Request.Headers["X-Readiness-Tenant"], out var id) ? id : null;
    }

    private sealed class HeaderModuleAccessor(IHttpContextAccessor context) : ITenantModuleAccessor
    {
        public Task<IReadOnlySet<string>> GetActiveModuleKeysAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(context.HttpContext?.Request.Headers.ContainsKey("X-Readiness-Rentals") == true
                ? new HashSet<string>(StringComparer.Ordinal) { PlatformModules.Rentals }
                : new HashSet<string>(StringComparer.Ordinal));
    }

    private sealed class HeaderPermissionHandler : AuthorizationHandler<PermissionRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
        {
            if (context.User.HasClaim("perm", requirement.PermissionKey)) context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("X-Readiness-Auth")) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = Request.Headers["X-Readiness-Permission"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => new Claim("perm", value));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }

    private sealed class ReadinessControllerProvider : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) => typeInfo.AsType() == typeof(RentalsReadinessController);
    }
}
