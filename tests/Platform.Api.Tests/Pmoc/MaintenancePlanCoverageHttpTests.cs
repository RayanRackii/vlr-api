using System.Net;
using System.Reflection;
using System.Security.Claims;
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
using Platform.Api.Modules.Pmoc.Controllers;
using Platform.Api.Modules.Pmoc.Dtos;
using Platform.Api.Modules.Pmoc.Services;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Pmoc;

public sealed class MaintenancePlanCoverageHttpTests
{
    [Fact]
    public void Coverage_requires_plans_read_permission()
    {
        var method = typeof(MaintenancePlansController).GetMethod(
            nameof(MaintenancePlansController.GetCoverage));
        Assert.NotNull(method);
        var permission = method!.GetCustomAttribute<RequirePermissionAttribute>();
        Assert.NotNull(permission);
        Assert.Equal(PermissionPolicies.Name(Permissions.Pmoc.PlansRead), permission!.Policy);
        Assert.Equal(
            PlatformModules.Pmoc,
            typeof(MaintenancePlansController).GetCustomAttribute<RequireActiveModuleAttribute>()!.ModuleKey);
    }

    [Fact]
    public void Technician_legacy_keys_do_not_include_pmoc_plans_read()
    {
        Assert.DoesNotContain(Permissions.Pmoc.PlansRead, PermissionCatalog.TechnicianLegacyKeys);
        Assert.DoesNotContain(Permissions.Pmoc.PlansWrite, PermissionCatalog.TechnicianLegacyKeys);
        Assert.DoesNotContain(Permissions.Pmoc.TemplatesRead, PermissionCatalog.TechnicianLegacyKeys);
    }

    [Fact]
    public async Task Coverage_without_plans_read_returns_403()
    {
        using var host = StartHost(Permissions.Os.WorkOrdersRead);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "ops@club.test");

        var response = await client.GetAsync($"/api/maintenance-plans/{Guid.NewGuid()}/coverage");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Coverage_with_technician_keys_returns_403()
    {
        using var host = StartHost([.. PermissionCatalog.TechnicianLegacyKeys]);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "tech@club.test");

        var response = await client.GetAsync($"/api/maintenance-plans/{Guid.NewGuid()}/coverage");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Coverage_with_plans_read_returns_404_for_missing_plan()
    {
        using var host = StartHost(Permissions.Pmoc.PlansRead);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "manager@club.test");

        var response = await client.GetAsync($"/api/maintenance-plans/{Guid.NewGuid()}/coverage");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static IHost StartHost(params string[] allowedPermissionKeys) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddSingleton<IMaintenancePlanService, StubMaintenancePlanService>();
                    services.AddSingleton<IMaintenancePlanCoverageService, StubCoverageService>();
                    services.AddSingleton<IAssetRegistry, StubAssetRegistry>();
                    services.AddSingleton<ITenantProvider, StubTenantProvider>();
                    services.AddSingleton<ITenantModuleAccessor, StubPmocModules>();
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

                            manager.ApplicationParts.Add(new AssemblyPart(typeof(MaintenancePlansController).Assembly));
                            manager.FeatureProviders.Add(
                                new MultiControllerFeatureProvider(typeof(MaintenancePlansController)));
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

    private sealed class StubCoverageService : IMaintenancePlanCoverageService
    {
        public Task<MaintenancePlanCoverageResponse?> GetCoverageAsync(
            Guid planId,
            IReadOnlyCollection<PmocOperationalStatus>? statuses,
            CancellationToken cancellationToken) =>
            Task.FromResult<MaintenancePlanCoverageResponse?>(null);
    }

    private sealed class StubMaintenancePlanService : IMaintenancePlanService
    {
        public Task<IReadOnlyList<MaintenancePlanResponse>> ListAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MaintenancePlanResponse>>([]);

        public Task<MaintenancePlanResponse?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MaintenancePlanResponse> CreatePlanWithTasksAsync(
            CreateMaintenancePlanRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MaintenancePlanResponse> CreateFromTemplateAsync(
            CreateFromTemplateRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MaintenancePlanResponse?> UpdateAsync(
            Guid id,
            UpdateMaintenancePlanRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MaintenancePlanResponse?> ReplaceTasksAsync(
            Guid id,
            ReplacePlanTasksRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubAssetRegistry : IAssetRegistry
    {
        public Task<IReadOnlyList<RegistryCategoryListItem>> ListCategoriesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RegistryCategoryListItem>>([]);

        public Task<IReadOnlyList<RegistryAssetListItem>> ListAssetsAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AssetFamilyDetailResponse>> ListActiveFamiliesAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Asset> RequireAssetAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AssetCategory> RequireCategoryAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Asset> CreateRentableAsync(
            CreateRentableRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Asset> UpdateRentableAsync(
            Guid rentalAssetId,
            UpdateRentableRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubTenantProvider : ITenantProvider
    {
        public Guid? TenantId => Guid.Parse("11111111-1111-1111-1111-111111111111");
    }

    private sealed class StubPmocModules : ITenantModuleAccessor
    {
        public Task<IReadOnlySet<string>> GetActiveModuleKeysAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(
                new HashSet<string>(StringComparer.Ordinal) { PlatformModules.Pmoc });
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
