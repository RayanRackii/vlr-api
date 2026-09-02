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
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Modules.Assets.Services;
using Platform.Api.Tests.Fakes;

namespace Platform.Api.Tests.Admin;

public sealed class AdminAssetFamiliesControllerTests
{
    private const string AdminEmail = "admin@rolvix.test";
    private const string StaffEmail = "staff@club.test";

    private static readonly Guid SpacesId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public async Task PlatformAdmin_without_tenant_returns_200_catalog()
    {
        using var host = StartHost();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, AdminEmail);

        var response = await client.GetAsync("/api/admin/asset-families");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);

        var item = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal(SpacesId, item.GetProperty("id").GetGuid());
        Assert.Equal("spaces", item.GetProperty("key").GetString());
        Assert.Equal("Spaces", item.GetProperty("label").GetString());
        Assert.Equal(JsonValueKind.Array, item.GetProperty("fields").ValueKind);
        Assert.Equal(0, item.GetProperty("fields").GetArrayLength());
        Assert.Equal(1, item.GetProperty("sortOrder").GetInt32());
        Assert.True(item.GetProperty("isActive").GetBoolean());
        Assert.False(HasProperty(item, "tenantId"));
        Assert.False(HasProperty(item, "TenantId"));
    }

    [Fact]
    public async Task Authenticated_tenant_admin_not_on_allowlist_returns_403()
    {
        using var host = StartHost();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, StaffEmail);

        var response = await client.GetAsync("/api/admin/asset-families");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Missing_auth_header_returns_401()
    {
        using var host = StartHost();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/api/admin/asset-families");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static IHost StartHost() =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddSingleton<IAssetFamilyService, FakeCatalogAssetFamilyService>();
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
                                new AssemblyPart(typeof(AdminAssetFamiliesController).Assembly));
                            manager.FeatureProviders.Add(
                                new SingleControllerFeatureProvider(typeof(AdminAssetFamiliesController)));
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

    private static bool HasProperty(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class FakeCatalogAssetFamilyService : IAssetFamilyService
    {
        public Task<IReadOnlyList<AssetFamilyDetailResponse>> ListCatalogAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AssetFamilyDetailResponse>>(
            [
                new AssetFamilyDetailResponse(
                    SpacesId,
                    "spaces",
                    "Spaces",
                    [],
                    SortOrder: 1,
                    IsActive: true),
            ]);

        public Task<IReadOnlyList<AssetFamilyDetailResponse>> ListActiveForTenantAsync(
            Guid tenantId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

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
