using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
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
using Platform.Api.Authorization;
using Platform.Api.Modules.Assets.Controllers;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Modules.Assets.Services;
using Platform.Core.Domain.Constants;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Assets;

public sealed class InventoryHttpGateTests
{
    [Fact]
    public async Task Get_assets_without_inventory_assets_read_returns_403()
    {
        using var host = StartHost(Permissions.Rentals.AssetsWrite);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "ops@club.test");

        var response = await client.GetAsync("/api/assets");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_asset_categories_without_inventory_categories_read_returns_403()
    {
        using var host = StartHost(Permissions.Rentals.AssetsWrite);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "ops@club.test");

        var response = await client.GetAsync("/api/asset-categories");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Post_assets_without_inventory_assets_write_returns_403()
    {
        using var host = StartHost(Permissions.Rentals.AssetsWrite);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "ops@club.test");

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/assets", content);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_asset_families_without_inventory_families_read_returns_403()
    {
        using var host = StartHost(Permissions.Rentals.AssetsWrite);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "ops@club.test");

        var response = await client.GetAsync("/api/asset-families");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static IHost StartHost(params string[] allowedPermissionKeys) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddSingleton<IAssetService, StubAssetService>();
                    services.AddSingleton<IAssetCategoryService, StubAssetCategoryService>();
                    services.AddSingleton<IAssetFamilyService, StubAssetFamilyService>();
                    services.AddSingleton<ITenantProvider, StubTenantProvider>();
                    services.AddSingleton<IAuthorizationHandler>(
                        new AllowlistedPermissionHandler(allowedPermissionKeys));
                    services.AddAuthentication(SupabaseJwtBearerDefaults.AuthenticationScheme)
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                            SupabaseJwtBearerDefaults.AuthenticationScheme,
                            _ => { });
                    services.AddAuthorization(options => options.AddRolvixPolicies());
                    services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
                    services.AddControllers()
                        .ConfigureApplicationPartManager(manager =>
                        {
                            manager.ApplicationParts.Clear();
                            foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
                            {
                                manager.FeatureProviders.Remove(provider);
                            }

                            manager.ApplicationParts.Add(new AssemblyPart(typeof(AssetsController).Assembly));
                            manager.FeatureProviders.Add(
                                new MultiControllerFeatureProvider(
                                    typeof(AssetsController),
                                    typeof(AssetCategoriesController),
                                    typeof(AssetFamiliesController)));
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

    private sealed class AllowlistedPermissionHandler(IReadOnlyCollection<string> allowedKeys)
        : AuthorizationHandler<PermissionRequirement>
    {
        protected override Task HandleRequirementAsync(
            AuthorizationHandlerContext context,
            PermissionRequirement requirement)
        {
            if (allowedKeys.Contains(requirement.PermissionKey))
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class StubAssetService : IAssetService
    {
        public Task<IReadOnlyList<AssetResponse>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AssetResponse>>([]);

        public Task<AssetResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AssetResponse> CreateAsync(
            CreateAssetRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AssetResponse?> UpdateAsync(
            Guid id,
            UpdateAssetRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DeleteAssetResult?> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BulkCreateAssetsResponse> BulkCreateAsync(
            BulkCreateAssetsRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AssetResponse> CreateRentableAsync(
            CreateRentableRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AssetResponse> UpdateRentableAsync(
            Guid id,
            UpdateRentableRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubAssetCategoryService : IAssetCategoryService
    {
        public Task<IReadOnlyList<AssetCategoryResponse>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AssetCategoryResponse>>([]);

        public Task<AssetCategoryResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AssetCategoryResponse> CreateAsync(
            CreateAssetCategoryRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AssetCategoryResponse?> UpdateAsync(
            Guid id,
            UpdateAssetCategoryRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DeleteAssetCategoryResult?> DeleteAsync(
            Guid id,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubAssetFamilyService : IAssetFamilyService
    {
        public Task<IReadOnlyList<AssetFamilyDetailResponse>> ListCatalogAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AssetFamilyDetailResponse>>([]);

        public Task<IReadOnlyList<AssetFamilyDetailResponse>> ListActiveForTenantAsync(
            Guid tenantId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AssetFamilyDetailResponse>>([]);
    }

    private sealed class StubTenantProvider : ITenantProvider
    {
        public Guid? TenantId => Guid.Parse("11111111-1111-1111-1111-111111111111");
    }

    private sealed class MultiControllerFeatureProvider(params Type[] controllerTypes)
        : ControllerFeatureProvider
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
                new("sub", "test-sub"),
            ];

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
