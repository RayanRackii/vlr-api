using System.Net;
using System.Net.Http.Json;
using System.Reflection;
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
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Modules.Assets.Services;
using Platform.Api.Modules.Rentals.Controllers;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Serialization;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Rentals;

public sealed class RentalAssetCategoriesHttpTests
{
    [Fact]
    public async Task Post_categories_with_rentals_write_returns_201_registry_shape()
    {
        using var host = StartHost([PlatformModules.Rentals], Permissions.Rentals.AssetsWrite);

        var response = await PostCategoryAsync(host, "Piscina");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var category = await response.Content.ReadFromJsonAsync<RegistryCategoryListItem>();
        Assert.NotNull(category);
        Assert.Equal("Piscina", category.Name);
        Assert.NotEqual(Guid.Empty, category.Id);
    }

    [Theory]
    [InlineData("   ", HttpStatusCode.BadRequest)]
    [InlineData("Quadras", HttpStatusCode.Conflict)]
    public async Task Post_categories_maps_registry_validation_errors(string name, HttpStatusCode expectedStatus)
    {
        using var host = StartHost([PlatformModules.Rentals], Permissions.Rentals.AssetsWrite);

        var response = await PostCategoryAsync(host, name);

        Assert.Equal(expectedStatus, response.StatusCode);
    }

    [Fact]
    public async Task Post_categories_without_rentals_write_returns_403()
    {
        using var host = StartHost([PlatformModules.Rentals]);

        var response = await PostCategoryAsync(host, "Piscina");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Post_categories_when_rentals_module_is_inactive_returns_403()
    {
        using var host = StartHost([], Permissions.Rentals.AssetsWrite);

        var response = await PostCategoryAsync(host, "Piscina");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostCategoryAsync(IHost host, string name)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "ops@club.test");
        using var content = new StringContent($$"""{"name":"{{name}}"}""", Encoding.UTF8, "application/json");
        return await client.PostAsync("/api/rental-assets/categories", content);
    }

    private static IHost StartHost(
        IReadOnlyCollection<string> activeModules,
        params string[] allowedPermissions) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddSingleton<IAssetRegistry, StubAssetRegistry>();
                    services.AddSingleton<IRentalAssetService, StubRentalAssetService>();
                    services.AddSingleton<IReservationQueueService, StubReservationQueueService>();
                    services.AddSingleton<ITenantProvider, StubTenantProvider>();
                    services.AddSingleton<ITenantModuleAccessor>(new StubModules(activeModules));
                    services.AddSingleton<IAuthorizationHandler>(
                        new AllowlistedPermissionHandler(allowedPermissions));
                    services.AddAuthentication(SupabaseJwtBearerDefaults.AuthenticationScheme)
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                            SupabaseJwtBearerDefaults.AuthenticationScheme,
                            _ => { });
                    services.AddAuthorization(options => options.AddRolvixPolicies());
                    services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
                    services.AddSingleton<IAuthorizationMiddlewareResultHandler, JsonForbiddenAuthorizationResultHandler>();
                    services.AddControllers()
                        .ConfigureApplicationPartManager(manager =>
                        {
                            manager.ApplicationParts.Clear();
                            foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
                            {
                                manager.FeatureProviders.Remove(provider);
                            }

                            manager.ApplicationParts.Add(new AssemblyPart(typeof(RentalAssetsController).Assembly));
                            manager.FeatureProviders.Add(new SingleControllerFeatureProvider(typeof(RentalAssetsController)));
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

    private sealed class StubAssetRegistry : IAssetRegistry
    {
        public Task<RegistryCategoryListItem> CreateCategoryAsync(
            CreateAssetCategoryRequest request,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                throw new ArgumentException("Asset category name is required.");
            }

            if (request.Name == "Quadras")
            {
                throw new InvalidOperationException("An asset category with this name already exists.");
            }

            return Task.FromResult(new RegistryCategoryListItem(Guid.NewGuid(), request.Name.Trim()));
        }

        public Task<IReadOnlyList<RegistryCategoryListItem>> ListCategoriesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RegistryCategoryListItem>>([]);

        public Task<IReadOnlyList<RegistryAssetListItem>> ListAssetsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RegistryAssetListItem>>([]);

        public Task<IReadOnlyList<AssetFamilyDetailResponse>> ListActiveFamiliesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AssetFamilyDetailResponse>>([]);

        public Task<Asset> RequireAssetAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AssetCategory> RequireCategoryAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Asset> CreateRentableAsync(CreateRentableRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Asset> UpdateRentableAsync(Guid rentalAssetId, UpdateRentableRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubRentalAssetService : IRentalAssetService
    {
        public Task<IReadOnlyList<RentalAssetResponse>> ListRentableAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RentalAssetResponse>>([]);

        public Task<RentalAssetResponse?> GetByAssetIdAsync(Guid assetId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RentalAssetResponse> UpdateSchedulePolicyAsync(Guid rentalAssetId, UpdateRentalSchedulePolicyRequestDto request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BulkUpdateRentalSchedulePolicyResponseDto> UpdateSchedulePolicyBulkAsync(BulkUpdateRentalSchedulePolicyRequestDto request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RentalAssetResponse> SetMaxAdvanceBookingDaysAsync(Guid rentalAssetId, JsonOptional<int> maxAdvanceBookingDays, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubReservationQueueService : IReservationQueueService
    {
        public Task<ReservationQueueStatusDto> GetStatusAsync(Guid rentalAssetId, Guid customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReservationQueueStatusDto> JoinAsync(Guid rentalAssetId, Guid customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ReservationQueueStatusDto> LeaveAsync(Guid rentalAssetId, Guid customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task EnsureActiveTurnForBookingAsync(Guid customerId, RentalAsset rentalAsset, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteTurnAsync(Guid customerId, RentalAsset rentalAsset, Guid reservationId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubTenantProvider : ITenantProvider
    {
        public Guid? TenantId => Guid.Parse("11111111-1111-1111-1111-111111111111");
    }

    private sealed class StubModules(IReadOnlyCollection<string> activeModules) : ITenantModuleAccessor
    {
        public Task<IReadOnlySet<string>> GetActiveModuleKeysAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(activeModules.ToHashSet(StringComparer.Ordinal));
    }

    private sealed class AllowlistedPermissionHandler(IReadOnlyCollection<string> allowedKeys)
        : AuthorizationHandler<PermissionRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
        {
            if (allowedKeys.Contains(requirement.PermissionKey))
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string HeaderName = "X-Test-User";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Request.Headers.ContainsKey(HeaderName)
                ? Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                    new System.Security.Claims.ClaimsPrincipal(
                        new System.Security.Claims.ClaimsIdentity("Test")),
                    Scheme.Name)))
                : Task.FromResult(AuthenticateResult.NoResult());
    }

    private sealed class SingleControllerFeatureProvider(Type controllerType) : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) => typeInfo.AsType() == controllerType;
    }
}
