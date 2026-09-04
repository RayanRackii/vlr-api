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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Modules.Admin.Controllers;
using Platform.Api.Tests.Fakes;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Tests.Admin;

public sealed class AdminModulesControllerTests
{
    private const string AdminEmail = "admin@rolvix.test";
    private const string StaffEmail = "staff@club.test";
    private const string AssetRegistry = "asset-registry";

    [Fact]
    public async Task PlatformAdmin_without_tenant_returns_200_catalog()
    {
        using var host = StartHost();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, AdminEmail);

        var response = await client.GetAsync("/api/admin/modules");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Asset Registry", body, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);

        var byKey = doc.RootElement
            .EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("key").GetString() ?? string.Empty,
                StringComparer.Ordinal);

        Assert.True(byKey.ContainsKey(PlatformModules.Inventory));
        Assert.True(byKey.ContainsKey(PlatformModules.Pmoc));
        Assert.True(byKey.ContainsKey(PlatformModules.WorkOrders));
        Assert.True(byKey.ContainsKey(PlatformModules.Rentals));
        Assert.True(byKey.ContainsKey(PlatformModules.Catalog));
        Assert.False(byKey.ContainsKey(AssetRegistry));

        AssertProvides(byKey[PlatformModules.Inventory], AssetRegistry);
        AssertRequires(byKey[PlatformModules.Rentals], AssetRegistry);
        AssertRequires(byKey[PlatformModules.Pmoc], AssetRegistry);
        AssertRequires(byKey[PlatformModules.WorkOrders], AssetRegistry);
        Assert.Equal(0, byKey[PlatformModules.Catalog].GetProperty("requiredCapabilities").GetArrayLength());
        Assert.Equal(0, byKey[PlatformModules.Catalog].GetProperty("provides").GetArrayLength());

        if (byKey.TryGetValue(PlatformModules.Maintenance, out var maintenance))
        {
            Assert.True(maintenance.GetProperty("isLegacy").GetBoolean());
            Assert.False(maintenance.GetProperty("isCommercial").GetBoolean());
        }

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var isLegacy = item.GetProperty("isLegacy").GetBoolean();
            var isCommercial = item.GetProperty("isCommercial").GetBoolean();
            if (isCommercial && !isLegacy)
            {
                Assert.NotEqual(PlatformModules.Maintenance, item.GetProperty("key").GetString());
            }
        }
    }

    [Fact]
    public async Task Authenticated_tenant_admin_not_on_allowlist_returns_403()
    {
        using var host = StartHost();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, StaffEmail);

        var response = await client.GetAsync("/api/admin/modules");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Missing_auth_header_returns_401()
    {
        using var host = StartHost();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/api/admin/modules");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static void AssertProvides(JsonElement item, string capability)
    {
        var provides = item.GetProperty("provides")
            .EnumerateArray()
            .Select(v => v.GetString())
            .ToList();
        Assert.Contains(capability, provides);
    }

    private static void AssertRequires(JsonElement item, string capability)
    {
        var required = item.GetProperty("requiredCapabilities")
            .EnumerateArray()
            .Select(v => v.GetString())
            .ToList();
        Assert.Contains(capability, required);
    }

    private static IHost StartHost() =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddSingleton<IPlatformAdminChecker>(_ => new FakePlatformAdminChecker(AdminEmail));
                    services.AddSingleton<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
                    services.AddAuthentication(SupabaseJwtBearerDefaults.AuthenticationScheme)
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                            SupabaseJwtBearerDefaults.AuthenticationScheme,
                            _ => { });
                    services.AddAuthorization(options => options.AddRolvixPolicies());
                    services.AddControllers()
                        .ConfigureApplicationPartManager(manager =>
                        {
                            manager.ApplicationParts.Clear();
                            foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
                            {
                                manager.FeatureProviders.Remove(provider);
                            }

                            manager.ApplicationParts.Add(
                                new AssemblyPart(typeof(AdminModulesController).Assembly));
                            manager.FeatureProviders.Add(
                                new SingleControllerFeatureProvider(typeof(AdminModulesController)));
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

    private sealed class SingleControllerFeatureProvider(Type controllerType)
        : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) =>
            typeInfo.AsType() == controllerType;
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string HeaderName = "X-Test-User";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(HeaderName, out var values))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var email = values.ToString();
            if (string.IsNullOrWhiteSpace(email))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            Claim[] claims =
            [
                new("email", email),
                new(ClaimTypes.Email, email),
                new(ClaimTypes.Name, email),
            ];

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
