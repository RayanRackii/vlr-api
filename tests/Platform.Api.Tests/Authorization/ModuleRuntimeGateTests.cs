using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Modules.Admin.Controllers;
using Platform.Api.Modules.Assets.Controllers;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Modules.Assets.Services;
using Platform.Api.Modules.Catalog.Controllers;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Modules.Pmoc.Controllers;
using Platform.Api.Modules.Pmoc.Dtos;
using Platform.Api.Modules.Pmoc.Services;
using Platform.Api.Modules.Rentals.Controllers;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Modules.Users.Controllers;
using Platform.Api.Modules.Users.Dtos;
using Platform.Api.Modules.Users.Services;
using Platform.Api.Modules.WorkOrders.Controllers;
using Platform.Api.Modules.WorkOrders.Dtos;
using Platform.Api.Modules.WorkOrders.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Authorization;

public sealed class ModuleRuntimeGateTests
{
    private const string InactiveError = "Module is not active for this tenant.";
    private const string AdminEmail = "admin@rolvix.test";
    private const string StaffEmail = "ops@club.test";
    private const string StaffSub = "staff-sub";

    [Fact]
    public async Task B2B_module_on_with_permission_returns_200()
    {
        using var host = await StartHostAsync(
            tenantAModules: [PlatformModules.Inventory],
            allowedPermissions: [Permissions.Inventory.AssetsRead]);
        var response = await GetB2BAsync(host, "/api/assets");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task B2B_module_off_returns_module_403_even_when_permission_is_allowlisted()
    {
        using var host = await StartHostAsync(
            tenantAModules: [],
            allowedPermissions: [Permissions.Inventory.AssetsRead]);
        await AssertModuleInactiveAsync(await GetB2BAsync(host, "/api/assets"));
    }

    [Fact]
    public async Task B2B_module_on_without_permission_returns_rbac_403()
    {
        using var host = await StartHostAsync(
            tenantAModules: [PlatformModules.Inventory],
            allowedPermissions: []);
        var response = await GetB2BAsync(host, "/api/assets");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(RbacErrorCodes.Forbidden, doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task B2C_catalog_active_returns_200()
    {
        using var host = await StartHostAsync(tenantAModules: [PlatformModules.Catalog]);
        var seed = host.Services.GetRequiredService<SeededGate>();
        var response = await GetCustomerAsync(host, "/api/catalog/portal/products", seed.CustomerAId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task B2C_catalog_inactive_returns_module_403()
    {
        using var host = await StartHostAsync(tenantAModules: []);
        var seed = host.Services.GetRequiredService<SeededGate>();
        await AssertModuleInactiveAsync(
            await GetCustomerAsync(host, "/api/catalog/portal/products", seed.CustomerAId));
    }

    [Fact]
    public async Task B2C_rentals_active_returns_200()
    {
        using var host = await StartHostAsync(tenantAModules: [PlatformModules.Rentals]);
        var seed = host.Services.GetRequiredService<SeededGate>();
        var response = await GetCustomerAsync(host, "/api/reservations/mine", seed.CustomerAId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task B2C_rentals_inactive_returns_module_403()
    {
        using var host = await StartHostAsync(tenantAModules: []);
        var seed = host.Services.GetRequiredService<SeededGate>();
        await AssertModuleInactiveAsync(
            await GetCustomerAsync(host, "/api/reservations/mine", seed.CustomerAId));
    }

    [Fact]
    public async Task Anonymous_public_module_on_returns_200()
    {
        using var host = await StartHostAsync(tenantAModules: [PlatformModules.Rentals]);
        var seed = host.Services.GetRequiredService<SeededGate>();
        var client = host.GetTestClient();
        var response = await client.GetAsync($"/api/public/tenants/{seed.TenantA.Subdomain}/rental-assets");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_public_module_off_returns_module_403()
    {
        using var host = await StartHostAsync(tenantAModules: [PlatformModules.Catalog]);
        var seed = host.Services.GetRequiredService<SeededGate>();
        var client = host.GetTestClient();
        await AssertModuleInactiveAsync(
            await client.GetAsync($"/api/public/tenants/{seed.TenantA.Subdomain}/rental-assets"));
    }

    [Fact]
    public async Task Customer_jwt_for_tenant_A_cannot_open_public_rentals_of_inactive_tenant_B()
    {
        using var host = await StartHostAsync(
            tenantAModules: [PlatformModules.Rentals],
            tenantBModules: [PlatformModules.Catalog]);
        var seed = host.Services.GetRequiredService<SeededGate>();

        await AssertModuleInactiveAsync(
            await GetCustomerAsync(
                host,
                $"/api/public/tenants/{seed.TenantB.Subdomain}/rental-assets",
                seed.CustomerAId,
                seed.TenantA.Id));
    }

    [Fact]
    public async Task Customer_jwt_for_tenant_A_cannot_open_availability_of_inactive_tenant_B()
    {
        using var host = await StartHostAsync(
            tenantAModules: [PlatformModules.Rentals],
            tenantBModules: [PlatformModules.Catalog]);
        var seed = host.Services.GetRequiredService<SeededGate>();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.CustomerHeader, seed.CustomerAId.ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, seed.TenantA.Id.ToString());
        client.DefaultRequestHeaders.Add(TenantHeaders.Subdomain, seed.TenantB.Subdomain!);

        await AssertModuleInactiveAsync(await client.GetAsync("/api/reservations/availability"));
    }

    [Fact]
    public async Task Customer_jwt_for_tenant_A_can_read_public_rentals_of_active_tenant_B()
    {
        using var host = await StartHostAsync(
            tenantAModules: [PlatformModules.Rentals],
            tenantBModules: [PlatformModules.Rentals]);
        var seed = host.Services.GetRequiredService<SeededGate>();

        var response = await GetCustomerAsync(
            host,
            $"/api/public/tenants/{seed.TenantB.Subdomain}/rental-assets",
            seed.CustomerAId,
            seed.TenantA.Id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PlatformAdmin_platform_mode_on_annotated_endpoint_is_not_200_from_gate_skip()
    {
        using var host = await StartHostAsync(
            tenantAModules: [PlatformModules.Inventory],
            allowedPermissions: [Permissions.Inventory.AssetsRead],
            realRbac: true);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, AdminEmail);
        var response = await client.GetAsync("/api/assets");
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Support_mode_module_off_returns_module_403()
    {
        using var host = await StartHostAsync(
            tenantAModules: [],
            allowedPermissions: [Permissions.Inventory.AssetsRead]);
        var seed = host.Services.GetRequiredService<SeededGate>();
        await AssertModuleInactiveAsync(await GetSupportAsync(host, "/api/assets", seed.TenantA.Id));
    }

    [Fact]
    public async Task Support_mode_module_on_returns_200()
    {
        using var host = await StartHostAsync(
            tenantAModules: [PlatformModules.Inventory],
            allowedPermissions: [Permissions.Inventory.AssetsRead]);
        var seed = host.Services.GetRequiredService<SeededGate>();
        var response = await GetSupportAsync(host, "/api/assets", seed.TenantA.Id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Catalog_portal_inactive_returns_module_403_after_catalog_gate_removal()
    {
        using var host = await StartHostAsync(tenantAModules: [PlatformModules.Rentals]);
        var seed = host.Services.GetRequiredService<SeededGate>();
        await AssertModuleInactiveAsync(
            await GetCustomerAsync(host, "/api/catalog/portal/products", seed.CustomerAId));
    }

    [Fact]
    public async Task Inventory_off_siblings_on_keeps_wave2_asset_registry_surfaces()
    {
        using var host = await StartHostAsync(
            tenantAModules:
            [
                PlatformModules.Rentals,
                PlatformModules.Pmoc,
                PlatformModules.WorkOrders,
            ],
            allowedPermissions:
            [
                Permissions.Inventory.AssetsRead,
                Permissions.Rentals.AssetsRead,
                Permissions.Pmoc.PlansRead,
                Permissions.Os.WorkOrdersRead,
            ]);

        await AssertModuleInactiveAsync(await GetB2BAsync(host, "/api/assets"));
        Assert.Equal(HttpStatusCode.OK, (await GetB2BAsync(host, "/api/rental-assets")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await GetB2BAsync(host, "/api/maintenance-plans/asset-categories")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetB2BAsync(host, "/api/work-orders/assets")).StatusCode);
    }

    [Fact]
    public async Task Inventory_on_does_not_substitute_for_siblings()
    {
        using var host = await StartHostAsync(
            tenantAModules: [PlatformModules.Inventory],
            allowedPermissions:
            [
                Permissions.Inventory.AssetsRead,
                Permissions.Rentals.AssetsRead,
                Permissions.Pmoc.PlansRead,
                Permissions.Os.WorkOrdersRead,
            ]);

        Assert.Equal(HttpStatusCode.OK, (await GetB2BAsync(host, "/api/assets")).StatusCode);
        await AssertModuleInactiveAsync(await GetB2BAsync(host, "/api/rental-assets"));
        await AssertModuleInactiveAsync(await GetB2BAsync(host, "/api/maintenance-plans/asset-categories"));
        await AssertModuleInactiveAsync(await GetB2BAsync(host, "/api/work-orders/assets"));
    }

    [Fact]
    public async Task Catalog_only_allows_catalog_and_forbids_siblings()
    {
        using var host = await StartHostAsync(
            tenantAModules: [PlatformModules.Catalog],
            allowedPermissions:
            [
                Permissions.Catalog.ProductsRead,
                Permissions.Inventory.AssetsRead,
                Permissions.Rentals.AssetsRead,
                Permissions.Pmoc.PlansRead,
                Permissions.Os.WorkOrdersRead,
            ]);

        Assert.Equal(HttpStatusCode.OK, (await GetB2BAsync(host, "/api/catalog/products")).StatusCode);
        await AssertModuleInactiveAsync(await GetB2BAsync(host, "/api/assets"));
        await AssertModuleInactiveAsync(await GetB2BAsync(host, "/api/rental-assets"));
        await AssertModuleInactiveAsync(await GetB2BAsync(host, "/api/maintenance-plans/asset-categories"));
        await AssertModuleInactiveAsync(await GetB2BAsync(host, "/api/work-orders/assets"));
    }

    [Theory]
    [InlineData(PlatformModules.Maintenance)]
    [InlineData(PlatformModuleCatalog.AssetRegistryCapability)]
    [InlineData("unknown-module")]
    [InlineData("orders")]
    [InlineData("pedidos")]
    [InlineData("Catálogo")]
    public void Startup_rejects_non_canonical_commercial_keys(string key)
    {
        Assert.False(ModuleGateStartupValidator.IsCanonicalCommercialKey(key));
    }

    [Fact]
    public void Startup_validate_throws_module_key_invalid_for_maintenance_controller()
    {
        using var host = StartValidatorHost(typeof(InvalidMaintenanceModuleController));
        var descriptors = host.Services.GetRequiredService<IActionDescriptorCollectionProvider>();
        var ex = Assert.Throws<InvalidOperationException>(() => ModuleGateStartupValidator.Validate(descriptors));
        Assert.Contains(ModuleGateStartupValidator.InvalidKeyCode, ex.Message, StringComparison.Ordinal);
        Assert.Contains(PlatformModules.Maintenance, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_assembly_module_keys_are_canonical_commercial()
    {
        ModuleGateStartupValidator.Validate(typeof(AssetsController).Assembly);
    }

    [Fact]
    public async Task B2C_customer_with_platform_admin_email_still_gets_module_403()
    {
        using var host = await StartHostAsync(tenantAModules: []);
        var seed = host.Services.GetRequiredService<SeededGate>();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.CustomerHeader, seed.CustomerAId.ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, seed.TenantA.Id.ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.CustomerEmailHeader, AdminEmail);
        await AssertModuleInactiveAsync(await client.GetAsync("/api/catalog/portal/products"));
    }

    [Fact]
    public async Task Accessor_loads_tenant_modules_once_when_resolver_and_accessor_share_scope()
    {
        await using var harness = await RbacResolverHarness.CreateAsync();
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.Rentals, isActive: true));
        var role = harness.AddCustomRole("Ops", Permissions.Rentals.AssetsRead);
        var user = harness.AddUser("once-user");
        harness.Assign(user, role);
        await harness.Db.SaveChangesAsync();

        var accessor = new TenantModuleAccessor(harness.Db, harness.TenantProvider);
        var resolver = new PermissionResolver(
            harness.Db,
            accessor,
            NullLogger<PermissionResolver>.Instance);

        await resolver.GetEffectivePermissionsAsync(harness.Tenant.Id, user.Id);
        await resolver.GetEnabledCatalogKeysAsync(harness.Tenant.Id);
        await accessor.GetActiveModuleKeysAsync();

        Assert.Equal(1, accessor.DatabaseLoadCount);
    }

    [Fact]
    public async Task PermissionResolver_reads_modules_from_accessor_not_stale_db_expectation()
    {
        await using var harness = await RbacResolverHarness.CreateAsync();
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.Inventory, isActive: true));
        await harness.Db.SaveChangesAsync();

        var accessor = new FixedModuleAccessor(PlatformModules.Rentals);
        var resolver = new PermissionResolver(
            harness.Db,
            accessor,
            NullLogger<PermissionResolver>.Instance);

        var catalog = await resolver.GetEnabledCatalogKeysAsync(harness.Tenant.Id);

        Assert.Equal(1, accessor.Calls);
        Assert.Contains(Permissions.Rentals.AssetsRead, catalog);
        Assert.DoesNotContain(Permissions.Inventory.AssetsRead, catalog);
    }

    [Fact]
    public async Task Core_users_me_is_available_without_commercial_modules()
    {
        using var host = await StartHostAsync(tenantAModules: []);
        var response = await GetB2BAsync(host, "/api/users/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Admin_modules_remains_available_to_platform_admin()
    {
        using var host = await StartHostAsync(tenantAModules: [PlatformModules.Catalog]);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, AdminEmail);
        var response = await client.GetAsync("/api/admin/modules");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Tenant_A_entitlement_does_not_satisfy_tenant_B()
    {
        using var host = await StartHostAsync(
            tenantAModules: [PlatformModules.Catalog],
            tenantBModules: []);
        var seed = host.Services.GetRequiredService<SeededGate>();

        Assert.Equal(
            HttpStatusCode.OK,
            (await GetCustomerAsync(host, "/api/catalog/portal/products", seed.CustomerAId, seed.TenantA.Id))
                .StatusCode);
        await AssertModuleInactiveAsync(
            await GetCustomerAsync(host, "/api/catalog/portal/products", seed.CustomerBId, seed.TenantB.Id));
    }

    [Fact]
    public void Commercial_module_controllers_are_annotated_and_core_admin_are_not()
    {
        AssertAnnotated<AssetsController>(PlatformModules.Inventory);
        AssertAnnotated<AssetCategoriesController>(PlatformModules.Inventory);
        AssertAnnotated<AssetFamiliesController>(PlatformModules.Inventory);
        AssertAnnotated<RentalAssetsController>(PlatformModules.Rentals);
        AssertAnnotated<RentalPricingsController>(PlatformModules.Rentals);
        AssertAnnotated<RentalPricingsBulkController>(PlatformModules.Rentals);
        AssertAnnotated<RentalLayoutsController>(PlatformModules.Rentals);
        AssertAnnotated<OccupancyKindsController>(PlatformModules.Rentals);
        AssertAnnotated<ScheduleController>(PlatformModules.Rentals);
        AssertAnnotated<ReservationsController>(PlatformModules.Rentals);
        AssertAnnotated<MaintenancePlansController>(PlatformModules.Pmoc);
        AssertAnnotated<GlobalTemplatesController>(PlatformModules.Pmoc);
        AssertAnnotated<WorkOrdersController>(PlatformModules.WorkOrders);
        AssertAnnotated<CatalogProductsController>(PlatformModules.Catalog);
        AssertAnnotated<CatalogOrdersController>(PlatformModules.Catalog);
        AssertAnnotated<CatalogNotificationsController>(PlatformModules.Catalog);
        AssertAnnotated<CatalogPortalController>(PlatformModules.Catalog);

        Assert.Null(typeof(UsersController).GetCustomAttribute<RequireActiveModuleAttribute>());
        Assert.Null(typeof(AdminModulesController).GetCustomAttribute<RequireActiveModuleAttribute>());
        Assert.Null(typeof(Platform.Api.Modules.Dashboard.Controllers.DashboardController)
            .GetCustomAttribute<RequireActiveModuleAttribute>());
        Assert.Null(typeof(Platform.Api.Modules.Admin.Controllers.AdminTenantsController)
            .GetCustomAttribute<RequireActiveModuleAttribute>());
        Assert.Null(typeof(Platform.Api.Modules.CustomerAuth.Controllers.CustomerAuthController)
            .GetCustomAttribute<RequireActiveModuleAttribute>());
        Assert.Null(typeof(Platform.Api.Modules.Webhooks.Controllers.WhatsAppWebhookController)
            .GetCustomAttribute<RequireActiveModuleAttribute>());
    }

    [Fact]
    public void Public_rental_assets_are_owned_by_reservations_controller()
    {
        var method = typeof(ReservationsController).GetMethod(nameof(ReservationsController.ListPublicAssets));
        Assert.NotNull(method);
        var route = method!.GetCustomAttribute<HttpGetAttribute>();
        Assert.NotNull(route);
        Assert.Contains("rental-assets", route!.Template, StringComparison.Ordinal);
        Assert.Equal(
            PlatformModules.Rentals,
            typeof(ReservationsController).GetCustomAttribute<RequireActiveModuleAttribute>()!.ModuleKey);
        Assert.Null(typeof(RentalAssetsController).GetMethod("ListPublicAssets"));
    }

    private static void AssertAnnotated<TController>(string moduleKey)
    {
        var attribute = typeof(TController).GetCustomAttribute<RequireActiveModuleAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal(moduleKey, attribute!.ModuleKey);
    }

    private static async Task AssertModuleInactiveAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(InactiveError, doc.RootElement.GetProperty("error").GetString());
    }

    private static async Task<HttpResponseMessage> GetB2BAsync(IHost host, string path)
    {
        var seed = host.Services.GetRequiredService<SeededGate>();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, StaffEmail);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, seed.TenantA.Id.ToString());
        return await client.GetAsync(path);
    }

    private static async Task<HttpResponseMessage> GetSupportAsync(IHost host, string path, Guid tenantId)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, AdminEmail);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, tenantId.ToString());
        return await client.GetAsync(path);
    }

    private static async Task<HttpResponseMessage> GetCustomerAsync(
        IHost host,
        string path,
        Guid customerId,
        Guid? tenantId = null)
    {
        var seed = host.Services.GetRequiredService<SeededGate>();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.CustomerHeader, customerId.ToString());
        client.DefaultRequestHeaders.Add(
            TestAuthHandler.TenantHeader,
            (tenantId ?? seed.TenantA.Id).ToString());
        return await client.GetAsync(path);
    }

    private static async Task<IHost> StartHostAsync(
        IReadOnlyCollection<string> tenantAModules,
        IReadOnlyCollection<string>? allowedPermissions = null,
        IReadOnlyCollection<string>? tenantBModules = null,
        bool realRbac = false)
    {
        var databaseName = $"module-gate-{Guid.NewGuid():N}";
        var seed = await SeedAsync(databaseName, tenantAModules, tenantBModules ?? []);

        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddSingleton(seed);
                    services.AddHttpContextAccessor();
                    services.AddScoped<AmbientTenantContext>();
                    services.AddSingleton<IPlatformAdminChecker>(_ => new FakePlatformAdminChecker(AdminEmail));
                    services.AddSingleton<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
                    services.AddScoped<ITenantProvider, HttpContextTenantProvider>();
                    services.AddScoped<AppDbContext>(sp =>
                    {
                        var options = new DbContextOptionsBuilder<AppDbContext>()
                            .UseInMemoryDatabase(databaseName)
                            .Options;
                        return new TestAppDbContext(options, sp.GetRequiredService<ITenantProvider>());
                    });
                    services.AddScoped<ITenantModuleAccessor, TenantModuleAccessor>();
                    services.AddScoped<IPublicTenantBinder, PublicTenantBinder>();
                    services.AddSingleton<IAssetService, StubAssetService>();
                    services.AddSingleton<IAssetCategoryService, StubAssetCategoryService>();
                    services.AddSingleton<IAssetFamilyService, StubAssetFamilyService>();
                    services.AddSingleton<IAssetRegistry, StubAssetRegistry>();
                    services.AddSingleton<IRentalAssetService, StubRentalAssetService>();
                    services.AddSingleton<IReservationQueueService, StubReservationQueueService>();
                    services.AddSingleton<IReservationService, StubReservationService>();
                    services.AddSingleton<IMaintenancePlanService, StubMaintenancePlanService>();
                    services.AddSingleton<IWorkOrderService, StubWorkOrderService>();
                    services.AddSingleton<ICatalogPortalService, StubCatalogPortalService>();
                    services.AddSingleton<ICatalogProductService, StubCatalogProductService>();
                    services.AddSingleton<IUserDirectoryService, StubUserDirectoryService>();
                    services.AddSingleton<IRbacActorAccessor, StubRbacActorAccessor>();

                    if (realRbac)
                    {
                        services.AddScoped<IPermissionResolver, PermissionResolver>();
                        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
                    }
                    else
                    {
                        services.AddSingleton<IAuthorizationHandler>(
                            new AllowlistedPermissionHandler(allowedPermissions ?? []));
                    }

                    services
                        .AddAuthentication(SupabaseJwtBearerDefaults.AuthenticationScheme)
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                            SupabaseJwtBearerDefaults.AuthenticationScheme,
                            _ => { })
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                            CustomerJwtBearerDefaults.AuthenticationScheme,
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

                            manager.ApplicationParts.Add(new AssemblyPart(typeof(AssetsController).Assembly));
                            manager.FeatureProviders.Add(
                                new MultiControllerFeatureProvider(
                                    typeof(AssetsController),
                                    typeof(RentalAssetsController),
                                    typeof(MaintenancePlansController),
                                    typeof(WorkOrdersController),
                                    typeof(CatalogPortalController),
                                    typeof(CatalogProductsController),
                                    typeof(ReservationsController),
                                    typeof(UsersController),
                                    typeof(AdminModulesController)));
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

        return host;
    }

    private static IHost StartValidatorHost(Type controllerType) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddControllers()
                        .ConfigureApplicationPartManager(manager =>
                        {
                            manager.ApplicationParts.Clear();
                            foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
                            {
                                manager.FeatureProviders.Remove(provider);
                            }

                            manager.ApplicationParts.Add(new AssemblyPart(controllerType.Assembly));
                            manager.FeatureProviders.Add(new MultiControllerFeatureProvider(controllerType));
                        });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .Start();

    private static async Task<SeededGate> SeedAsync(
        string databaseName,
        IReadOnlyCollection<string> tenantAModules,
        IReadOnlyCollection<string> tenantBModules)
    {
        var seedProvider = new FakeTenantProvider { TenantId = null };
        await using var db = InMemoryAppDb.Create(seedProvider, databaseName);

        var tenantA = new Tenant("Club A", "11111111000191", subdomain: "cluba");
        var tenantB = new Tenant("Club B", "22222222000191", subdomain: "clubb");
        db.Tenants.AddRange(tenantA, tenantB);

        foreach (var module in tenantAModules)
        {
            db.TenantModules.Add(new TenantModule(tenantA.Id, module, isActive: true));
        }

        foreach (var module in tenantBModules)
        {
            db.TenantModules.Add(new TenantModule(tenantB.Id, module, isActive: true));
        }

        var customerA = new Customer
        {
            TenantId = tenantA.Id,
            Name = "Ana",
            Email = "ana@club.test",
        };
        var customerB = new Customer
        {
            TenantId = tenantB.Id,
            Name = "Bruno",
            Email = "bruno@club.test",
        };
        db.Customers.AddRange(customerA, customerB);
        await db.SaveChangesAsync();

        return new SeededGate(tenantA, tenantB, customerA.Id, customerB.Id);
    }

    private sealed record SeededGate(Tenant TenantA, Tenant TenantB, Guid CustomerAId, Guid CustomerBId);

    private sealed class FixedModuleAccessor(params string[] keys) : ITenantModuleAccessor
    {
        public int Calls { get; private set; }

        public Task<IReadOnlySet<string>> GetActiveModuleKeysAsync(
            CancellationToken cancellationToken = default)
        {
            Calls++;
            IReadOnlySet<string> set = keys.ToHashSet(StringComparer.Ordinal);
            return Task.FromResult(set);
        }
    }

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
        public const string UserHeader = "X-Test-User";
        public const string TenantHeader = "X-Test-Tenant";
        public const string CustomerHeader = "X-Test-Customer";
        public const string CustomerEmailHeader = "X-Test-Customer-Email";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Scheme.Name == CustomerJwtBearerDefaults.AuthenticationScheme)
            {
                if (!Request.Headers.TryGetValue(CustomerHeader, out var customerValues)
                    || !Guid.TryParse(customerValues.ToString(), out var customerId))
                {
                    return Task.FromResult(AuthenticateResult.NoResult());
                }

                Request.Headers.TryGetValue(TenantHeader, out var tenantValues);
                Request.Headers.TryGetValue(CustomerEmailHeader, out var emailValues);
                var email = string.IsNullOrWhiteSpace(emailValues.ToString())
                    ? "member@club.test"
                    : emailValues.ToString();

                Claim[] customerClaims =
                [
                    new(CustomerClaimTypes.CustomerId, customerId.ToString()),
                    new(CustomerClaimTypes.TenantId, tenantValues.ToString()),
                    new(CustomerClaimTypes.Role, AuthRoles.Customer),
                    new(ClaimTypes.Role, AuthRoles.Customer),
                    new("email", email),
                    new(ClaimTypes.Email, email),
                    new("sub", customerId.ToString()),
                ];
                return Task.FromResult(
                    AuthenticateResult.Success(
                        new AuthenticationTicket(
                            new ClaimsPrincipal(new ClaimsIdentity(customerClaims, Scheme.Name)),
                            Scheme.Name)));
            }

            if (!Request.Headers.TryGetValue(UserHeader, out var userValues)
                || string.IsNullOrWhiteSpace(userValues.ToString()))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var staffEmail = userValues.ToString();
            var claims = new List<Claim>
            {
                new("email", staffEmail),
                new(ClaimTypes.Email, staffEmail),
                new(ClaimTypes.Name, staffEmail),
                new("sub", StaffSub),
            };

            if (Request.Headers.TryGetValue(TenantHeader, out var appTenant)
                && Guid.TryParse(appTenant.ToString(), out var tenantId))
            {
                claims.Add(
                    new Claim(
                        TenantClaimTypes.AppMetadata,
                        $"{{\"tenant_id\":\"{tenantId}\"}}"));
            }

            return Task.FromResult(
                AuthenticateResult.Success(
                    new AuthenticationTicket(
                        new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)),
                        Scheme.Name)));
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

    private sealed class StubAssetRegistry : IAssetRegistry
    {
        public Task<IReadOnlyList<RegistryCategoryListItem>> ListCategoriesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RegistryCategoryListItem>>([]);

        public Task<IReadOnlyList<RegistryAssetListItem>> ListAssetsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RegistryAssetListItem>>([]);

        public Task<IReadOnlyList<AssetFamilyDetailResponse>> ListActiveFamiliesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AssetFamilyDetailResponse>>([]);

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

    private sealed class StubRentalAssetService : IRentalAssetService
    {
        public Task<IReadOnlyList<RentalAssetResponse>> ListRentableAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RentalAssetResponse>>([]);

        public Task<RentalAssetResponse?> GetByAssetIdAsync(
            Guid assetId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RentalAssetResponse> UpdateSchedulePolicyAsync(
            Guid rentalAssetId,
            UpdateRentalSchedulePolicyRequestDto request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BulkUpdateRentalSchedulePolicyResponseDto> UpdateSchedulePolicyBulkAsync(
            BulkUpdateRentalSchedulePolicyRequestDto request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubReservationQueueService : IReservationQueueService
    {
        public Task<ReservationQueueStatusDto> GetStatusAsync(
            Guid rentalAssetId,
            Guid customerId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ReservationQueueStatusDto> JoinAsync(
            Guid rentalAssetId,
            Guid customerId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ReservationQueueStatusDto> LeaveAsync(
            Guid rentalAssetId,
            Guid customerId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task EnsureActiveTurnForBookingAsync(
            Guid customerId,
            RentalAsset rentalAsset,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CompleteTurnAsync(
            Guid customerId,
            RentalAsset rentalAsset,
            Guid reservationId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubReservationService : IReservationService
    {
        public Task<CheckAvailabilityResponseDto> CheckAvailabilityAsync(
            CheckAvailabilityRequestDto request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ReservationResponseDto> CreateReservationAsync(
            Guid customerId,
            CreateReservationRequestDto request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ReservationResponseDto>> ListMineAsync(
            Guid customerId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReservationResponseDto>>([]);

        public Task<IReadOnlyList<ReservationResponseDto>> ListAdminAsync(
            DateOnly? from,
            DateOnly? to,
            ReservationStatus? status,
            Guid? assetId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ReservationResponseDto> ConfirmAsync(
            Guid reservationId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ReservationResponseDto> CancelAsync(
            Guid reservationId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
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

        public Task<MaintenancePlanResponse?> UpdateAsync(
            Guid id,
            UpdateMaintenancePlanRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubWorkOrderService : IWorkOrderService
    {
        public Task<IReadOnlyList<WorkOrderResponse>> ListAsync(
            Guid? assetId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkOrderResponse>>([]);

        public Task<WorkOrderResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkOrderResponse> CreateAsync(
            CreateWorkOrderRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkOrderResponse?> UpdateTaskValueAsync(
            Guid workOrderId,
            Guid taskId,
            UpdateWorkOrderTaskValueRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkOrderResponse?> UpdateStatusAsync(
            Guid id,
            UpdateWorkOrderStatusRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubCatalogPortalService : ICatalogPortalService
    {
        public Task<IReadOnlyList<PortalProductResponse>> ListProductsAsync(
            string? search,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PortalProductResponse>>([]);

        public Task<PortalProductResponse?> GetProductAsync(
            Guid id,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CatalogOrderResponse> CreateOrderAsync(
            Guid customerId,
            CreatePortalOrderRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CatalogOrderResponse>> ListOrdersAsync(
            Guid customerId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CatalogOrderResponse?> GetOrderAsync(
            Guid customerId,
            Guid orderId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CatalogOrderResponse> CancelOrderAsync(
            Guid customerId,
            Guid orderId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProductRequestResponse> CreateProductRequestAsync(
            Guid customerId,
            CreateProductRequestDto request,
            IReadOnlyList<PortalUpload> files,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CatalogFileUrlResponse> GetOwnProductRequestFileUrlAsync(
            Guid customerId,
            Guid requestId,
            Guid fileId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubCatalogProductService : ICatalogProductService
    {
        public Task<IReadOnlyList<CatalogProductResponse>> ListAsync(
            CatalogProductListQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CatalogProductResponse>>([]);

        public Task<CatalogProductResponse?> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CatalogProductResponse> CreateAsync(
            CreateCatalogProductRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CatalogProductResponse?> UpdateAsync(
            Guid id,
            UpdateCatalogProductRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CatalogProductResponse?> DeactivateAsync(
            Guid id,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CatalogProductResponse?> ActivateAsync(
            Guid id,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CatalogProductFileDto> AddFileAsync(
            Guid productId,
            string fileName,
            string contentType,
            Stream content,
            CatalogFileVisibility visibility,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteFileAsync(
            Guid productId,
            Guid fileId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CatalogFileUrlResponse> GetFileUrlAsync(
            Guid productId,
            Guid fileId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubUserDirectoryService : IUserDirectoryService
    {
        public Task<CurrentUserResponse> GetCurrentAsync(
            ClaimsPrincipal principal,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new CurrentUserResponse(
                    Guid.NewGuid(),
                    "Ops",
                    StaffEmail,
                    ApplicationRoles.Admin,
                    null,
                    [],
                    [],
                    false,
                    null,
                    null,
                    false,
                    false,
                    [],
                    []));

        public Task<IReadOnlyList<TechnicianUserResponse>> ListTechniciansAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TenantMemberResponse>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AssignRolesAsync(
            Guid userId,
            RbacActor actor,
            IReadOnlyList<Guid> roleIds,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<InviteTenantMemberResponse> InviteAsync(
            RbacActor actor,
            InviteTenantMemberRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubRbacActorAccessor : IRbacActorAccessor
    {
        public Task<RbacActor> GetAsync(
            ClaimsPrincipal principal,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

[ApiController]
[RequireActiveModule(PlatformModules.Maintenance)]
[Route("api/test/invalid-maintenance")]
public sealed class InvalidMaintenanceModuleController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok();
}
