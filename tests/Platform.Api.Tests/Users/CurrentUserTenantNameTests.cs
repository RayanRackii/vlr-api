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
using Platform.Api.Modules.Users.Controllers;
using Platform.Api.Modules.Users.Services;
using Platform.Api.Notifications;
using Platform.Api.Services.Trial;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Users;

public sealed class CurrentUserTenantNameTests
{
    private const string StaffEmail = "staff@studio.test";
    private const string HomeOnlyEmail = "home-only@studio.test";
    private const string PlatformEmail = "platform@studio.test";
    private const string TradeA = "Estúdio Norte";
    private const string LegalA = "Estúdio Norte Ltda";
    private const string TradeB = "Galpão Sul";
    private const string LegalB = "Galpão Sul Ltda";
    private const string TaxA = "11111111000191";
    private const string TaxB = "22222222000191";

    [Fact]
    public async Task Staff_me_returns_only_the_callers_tenant_names()
    {
        var seed = await SeedAsync();
        using var host = await StartHostAsync(seed.DatabaseName);

        var response = await GetAsync(host, StaffEmail, seed.TenantA);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(seed.TenantA, doc.RootElement.GetProperty("tenantId").GetGuid());
        Assert.Equal(TradeA, doc.RootElement.GetProperty("tenantTradeName").GetString());
        Assert.Equal(LegalA, doc.RootElement.GetProperty("tenantLegalName").GetString());
        AssertOtherTenantAbsent(body);
        Assert.DoesNotContain(TaxA, body, StringComparison.Ordinal);
        Assert.DoesNotContain(TaxB, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Same_login_in_two_tenants_returns_only_the_active_tenant_names()
    {
        var seed = await SeedAsync();
        using var host = await StartHostAsync(seed.DatabaseName);

        var home = await GetAsync(host, StaffEmail, seed.TenantA);
        var away = await GetAsync(host, StaffEmail, seed.TenantB);
        var homeBody = await home.Content.ReadAsStringAsync();
        var awayBody = await away.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Equal(HttpStatusCode.OK, away.StatusCode);
        using var homeDoc = JsonDocument.Parse(homeBody);
        using var awayDoc = JsonDocument.Parse(awayBody);
        Assert.Equal(TradeA, homeDoc.RootElement.GetProperty("tenantTradeName").GetString());
        Assert.Equal(LegalA, homeDoc.RootElement.GetProperty("tenantLegalName").GetString());
        Assert.Equal(TradeB, awayDoc.RootElement.GetProperty("tenantTradeName").GetString());
        Assert.Equal(LegalB, awayDoc.RootElement.GetProperty("tenantLegalName").GetString());
        Assert.DoesNotContain(TradeB, homeBody, StringComparison.Ordinal);
        Assert.DoesNotContain(LegalB, homeBody, StringComparison.Ordinal);
        Assert.DoesNotContain(TradeA, awayBody, StringComparison.Ordinal);
        Assert.DoesNotContain(LegalA, awayBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Staff_me_for_another_tenant_does_not_return_that_tenants_names()
    {
        var seed = await SeedAsync();
        using var host = await StartHostAsync(seed.DatabaseName);

        var response = await GetAsync(host, HomeOnlyEmail, seed.TenantB);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertOtherTenantAbsent(body);
        Assert.DoesNotContain(TradeA, body, StringComparison.Ordinal);
        Assert.DoesNotContain(LegalA, body, StringComparison.Ordinal);
        Assert.DoesNotContain(TaxA, body, StringComparison.Ordinal);
        Assert.DoesNotContain(TaxB, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Platform_admin_without_tenant_returns_null_names()
    {
        var seed = await SeedAsync();
        using var host = await StartHostAsync(seed.DatabaseName);

        var response = await GetAsync(host, PlatformEmail, tenantId: null);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("tenantId").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("tenantTradeName").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("tenantLegalName").ValueKind);
        Assert.Equal("SUPER_ADMIN", doc.RootElement.GetProperty("role").GetString());
        Assert.DoesNotContain(TradeA, body, StringComparison.Ordinal);
        Assert.DoesNotContain(LegalA, body, StringComparison.Ordinal);
        AssertOtherTenantAbsent(body);
    }

    [Fact]
    public async Task Platform_admin_inside_a_tenant_returns_only_that_tenants_names()
    {
        var seed = await SeedAsync();
        using var host = await StartHostAsync(seed.DatabaseName);

        var response = await GetAsync(host, PlatformEmail, seed.TenantA);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(seed.TenantA, doc.RootElement.GetProperty("tenantId").GetGuid());
        Assert.Equal(TradeA, doc.RootElement.GetProperty("tenantTradeName").GetString());
        Assert.Equal(LegalA, doc.RootElement.GetProperty("tenantLegalName").GetString());
        AssertOtherTenantAbsent(body);
    }

    [Fact]
    public async Task Blank_trade_name_is_null_and_legal_name_is_trimmed()
    {
        var databaseName = $"me-names-blank-{Guid.NewGuid():N}";
        var provider = new FakeTenantProvider();
        await using (var db = InMemoryAppDb.Create(provider, databaseName))
        {
            var tenant = new Tenant(
                "  Estúdio Centro Ltda  ",
                "33333333000191",
                tradeName: "   ",
                subdomain: "centro");
            db.Tenants.Add(tenant);
            db.Users.Add(new User(tenant.Id, HomeOnlyEmail, "Staff User", HomeOnlyEmail));
            await db.SaveChangesAsync();

            using var host = await StartHostAsync(databaseName);
            var response = await GetAsync(host, HomeOnlyEmail, tenant.Id);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(body);
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("tenantTradeName").ValueKind);
            Assert.Equal("Estúdio Centro Ltda", doc.RootElement.GetProperty("tenantLegalName").GetString());
            Assert.DoesNotContain("33333333000191", body, StringComparison.Ordinal);
        }
    }

    private static void AssertOtherTenantAbsent(string body)
    {
        Assert.DoesNotContain(TradeB, body, StringComparison.Ordinal);
        Assert.DoesNotContain(LegalB, body, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> GetAsync(IHost host, string email, Guid? tenantId)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, email);
        if (tenantId is Guid id)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, id.ToString());
        }

        return await client.GetAsync("/api/users/me");
    }

    private static async Task<Seed> SeedAsync()
    {
        var databaseName = $"me-names-{Guid.NewGuid():N}";
        var provider = new FakeTenantProvider();
        await using var db = InMemoryAppDb.Create(provider, databaseName);
        var tenantA = new Tenant(LegalA, TaxA, TradeA, "estudio-norte");
        var tenantB = new Tenant(LegalB, TaxB, TradeB, "galpao-sul");
        db.Tenants.AddRange(tenantA, tenantB);
        db.Users.Add(new User(tenantA.Id, StaffEmail, "Staff User", StaffEmail));
        db.Users.Add(new User(tenantA.Id, HomeOnlyEmail, "Home Only", HomeOnlyEmail));
        db.Users.Add(new User(tenantB.Id, StaffEmail, "Staff Other", "staff.other@studio.test"));
        await db.SaveChangesAsync();
        return new Seed(databaseName, tenantA.Id, tenantB.Id);
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
                    services.AddScoped<PlatformAdminTenantSession>();
                    services.AddScoped<ITenantProvider, HttpContextTenantProvider>();
                    services.AddScoped<AppDbContext>(sp =>
                        InMemoryAppDb.Create(sp.GetRequiredService<ITenantProvider>(), databaseName));
                    services.AddScoped<ITenantModuleAccessor, TenantModuleAccessor>();
                    services.AddScoped<IPermissionResolver, PermissionResolver>();
                    services.AddScoped<IRbacGrantGuard, RbacGrantGuard>();
                    services.AddSingleton<ITrialGuard, FakeTrialGuard>();
                    services.AddScoped<IRbacActorAccessor, RbacActorAccessor>();
                    services.AddScoped<IUserDirectoryService, UserDirectoryService>();
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
                            manager.FeatureProviders.Add(new SelectedControllers(typeof(UsersController)));
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

    private sealed record Seed(string DatabaseName, Guid TenantA, Guid TenantB);

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
                Context.RequestServices.GetService<PlatformAdminTenantSession>()?.Resolve(tenantId);
            }

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
