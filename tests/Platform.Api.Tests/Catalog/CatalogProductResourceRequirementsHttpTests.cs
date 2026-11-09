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
using Platform.Api.Authorization;
using Platform.Api.Modules.Catalog.Controllers;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Catalog;

public sealed class CatalogProductResourceRequirementsHttpTests
{
    private const string ManagerEmail = "catalog-manager@club.test";
    private const string ReaderEmail = "catalog-reader@club.test";
    private const string SecretProduct = "Foreign Only Product";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Requirements_follow_catalog_and_allocation_stays_on_resource_planning()
    {
        Assert.Null(Method(typeof(CatalogProductsController), nameof(CatalogProductsController.GetResourceRequirements))
            .GetCustomAttribute<RequireActiveModuleAttribute>());
        Assert.Null(Method(typeof(CatalogProductsController), nameof(CatalogProductsController.ReplaceResourceRequirements))
            .GetCustomAttribute<RequireActiveModuleAttribute>());
        Assert.Equal(
            PlatformModules.Catalog,
            typeof(CatalogProductsController).GetCustomAttribute<RequireActiveModuleAttribute>()!.ModuleKey);
        Assert.Equal(
            PermissionPolicies.Name(Permissions.Catalog.ProductsManage),
            Method(typeof(CatalogProductsController), nameof(CatalogProductsController.GetResourceRequirements))
                .GetCustomAttribute<RequirePermissionAttribute>()!.Policy);
        Assert.Equal(
            PermissionPolicies.Name(Permissions.Catalog.ProductsManage),
            Method(typeof(CatalogProductsController), nameof(CatalogProductsController.ReplaceResourceRequirements))
                .GetCustomAttribute<RequirePermissionAttribute>()!.Policy);

        Assert.Equal(
            PlatformModules.ResourcePlanning,
            Method(typeof(CatalogOrdersController), nameof(CatalogOrdersController.AllocationPreview))
                .GetCustomAttribute<RequireActiveModuleAttribute>()!.ModuleKey);
        Assert.Equal(
            PlatformModules.ResourcePlanning,
            Method(typeof(CatalogOrdersController), nameof(CatalogOrdersController.AllocationUsage))
                .GetCustomAttribute<RequireActiveModuleAttribute>()!.ModuleKey);
    }

    [Fact]
    public async Task Catalog_on_without_resource_planning_reads_and_writes_requirements()
    {
        var world = await SeedAsync(catalogActive: true);
        using var host = StartHost(world.DatabaseName, allowPermissionDespiteModule: false);
        var client = Client(host, ManagerEmail, world.TenantId);

        var before = await client.GetFromJsonAsync<CatalogProductResourceRequirementsResponse>(
            Path(world.ProductId),
            JsonOptions);
        Assert.NotNull(before);
        Assert.Equal(0, before.ExpectedVersion);
        Assert.Empty(before.Requirements);

        var saved = await PutAsync(client, world.ProductId, world.RentalAssetId, expectedVersion: 0);
        Assert.Equal(1, saved.ExpectedVersion);
        var requirement = Assert.Single(saved.Requirements);
        Assert.Equal(world.RentalAssetId, requirement.RentalAssetId);
        Assert.Null(requirement.AssetCategoryId);
        Assert.Equal(1, requirement.ResourceQuantity);
        Assert.Equal(45, requirement.DurationMinutesPerUnit);
        Assert.Equal(5, requirement.PrepMinutes);
        Assert.Equal(10, requirement.CleanupMinutes);

        var after = await client.GetFromJsonAsync<CatalogProductResourceRequirementsResponse>(
            Path(world.ProductId),
            JsonOptions);
        Assert.Equal(saved.ExpectedVersion, after!.ExpectedVersion);
        Assert.Equal(requirement.Id, Assert.Single(after.Requirements).Id);

        await using var verify = Verify(world.DatabaseName, world.TenantId);
        var stored = await verify.CatalogProducts.SingleAsync(product => product.Id == world.ProductId);
        Assert.Equal(1, stored.ResourceRequirementsVersion);
        Assert.Equal(1, await verify.CatalogProductResourceRequirements.CountAsync(row => row.IsActive));
        Assert.DoesNotContain(
            await verify.TenantModules.Select(module => module.ModuleName).ToListAsync(),
            name => name == PlatformModules.ResourcePlanning);
    }

    [Fact]
    public async Task Catalog_off_returns_module_inactive_when_the_caller_is_authorized()
    {
        var world = await SeedAsync(catalogActive: false);
        using var host = StartHost(world.DatabaseName, allowPermissionDespiteModule: true);
        var client = Client(host, ManagerEmail, world.TenantId);

        await AssertModuleInactiveAsync(await client.GetAsync(Path(world.ProductId)), world.ProductName);
        await AssertModuleInactiveAsync(
            await client.PutAsJsonAsync(Path(world.ProductId), Body(world.RentalAssetId, 0), JsonOptions),
            world.ProductName);
    }

    [Fact]
    public async Task Catalog_off_staff_role_is_forbidden_before_the_action()
    {
        var world = await SeedAsync(catalogActive: false);
        using var host = StartHost(world.DatabaseName, allowPermissionDespiteModule: false);
        var client = Client(host, ManagerEmail, world.TenantId);

        var read = await client.GetAsync(Path(world.ProductId));
        var body = await read.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(RbacErrorCodes.Forbidden, ReadError(body));
        Assert.DoesNotContain(world.ProductName, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Other_tenant_product_and_resource_are_not_found()
    {
        var world = await SeedAsync(catalogActive: true);
        using var host = StartHost(world.DatabaseName, allowPermissionDespiteModule: false);
        var client = Client(host, ManagerEmail, world.TenantId);

        var read = await client.GetAsync(Path(world.ForeignProductId));
        var readBody = await read.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal("Product not found.", ReadError(readBody));
        Assert.DoesNotContain(SecretProduct, readBody, StringComparison.Ordinal);

        var write = await client.PutAsJsonAsync(
            Path(world.ForeignProductId),
            Body(world.RentalAssetId, 0),
            JsonOptions);
        var writeBody = await write.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
        Assert.Equal("Product not found.", ReadError(writeBody));
        Assert.DoesNotContain(SecretProduct, writeBody, StringComparison.Ordinal);

        var foreignResource = await client.PutAsJsonAsync(
            Path(world.ProductId),
            Body(world.ForeignRentalAssetId, 0),
            JsonOptions);
        var foreignBody = await foreignResource.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotFound, foreignResource.StatusCode);
        Assert.Equal($"Rental asset '{world.ForeignRentalAssetId}' was not found.", ReadError(foreignBody));
        Assert.DoesNotContain(SecretProduct, foreignBody, StringComparison.Ordinal);

        await using var home = Verify(world.DatabaseName, world.TenantId);
        Assert.Equal(0, (await home.CatalogProducts.SingleAsync()).ResourceRequirementsVersion);
        Assert.Empty(await home.CatalogProductResourceRequirements.ToListAsync());

        await using var foreign = Verify(world.DatabaseName, world.ForeignTenantId);
        Assert.Equal(0, (await foreign.CatalogProducts.SingleAsync()).ResourceRequirementsVersion);
        Assert.Empty(await foreign.CatalogProductResourceRequirements.ToListAsync());
    }

    [Fact]
    public async Task Missing_products_manage_is_forbidden()
    {
        var world = await SeedAsync(catalogActive: true);
        using var host = StartHost(world.DatabaseName, allowPermissionDespiteModule: false);
        var client = Client(host, ReaderEmail, world.TenantId);

        var read = await client.GetAsync(Path(world.ProductId));
        var readBody = await read.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(RbacErrorCodes.Forbidden, ReadError(readBody));
        Assert.DoesNotContain(world.ProductName, readBody, StringComparison.Ordinal);

        var write = await client.PutAsJsonAsync(
            Path(world.ProductId),
            Body(world.RentalAssetId, 0),
            JsonOptions);
        var writeBody = await write.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal(RbacErrorCodes.Forbidden, ReadError(writeBody));
        Assert.DoesNotContain(world.ProductName, writeBody, StringComparison.Ordinal);

        await using var verify = Verify(world.DatabaseName, world.TenantId);
        Assert.Equal(0, (await verify.CatalogProducts.SingleAsync()).ResourceRequirementsVersion);
    }

    private static async Task AssertModuleInactiveAsync(HttpResponseMessage response, string productName)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(RequireActiveModuleAttribute.InactiveModuleError, doc.RootElement.GetProperty("error").GetString());
        Assert.Equal(RequireActiveModuleAttribute.InactiveModuleCode, doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(PlatformModules.Catalog, doc.RootElement.GetProperty("module").GetString());
        Assert.DoesNotContain(productName, body, StringComparison.Ordinal);
    }

    private static async Task<CatalogProductResourceRequirementsResponse> PutAsync(
        HttpClient client,
        Guid productId,
        Guid rentalAssetId,
        int expectedVersion)
    {
        var response = await client.PutAsJsonAsync(Path(productId), Body(rentalAssetId, expectedVersion), JsonOptions);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonSerializer.Deserialize<CatalogProductResourceRequirementsResponse>(body, JsonOptions)!;
    }

    private static object Body(Guid rentalAssetId, int expectedVersion) => new
    {
        expectedVersion,
        requirements = new[]
        {
            new
            {
                rentalAssetId,
                resourceQuantity = 1,
                durationMinutesPerUnit = 45,
                prepMinutes = 5,
                cleanupMinutes = 10,
            },
        },
    };

    private static string Path(Guid productId) => $"/api/catalog/products/{productId}/resource-requirements";

    private static string? ReadError(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("error").GetString();
    }

    private static MethodInfo Method(Type controller, string name) =>
        controller.GetMethod(name) ?? throw new InvalidOperationException($"{controller.Name}.{name} is missing.");

    private static async Task<World> SeedAsync(bool catalogActive)
    {
        var databaseName = $"catalog-req-{Guid.NewGuid():N}";
        var provider = new FakeTenantProvider();
        await using var db = InMemoryAppDb.Create(provider, databaseName);

        var home = AddTenant(db, provider, "Catalog Club", catalogActive, "Home product");
        var foreign = AddTenant(db, provider, "Other Club", catalogActive: true, SecretProduct);
        AddStaff(db, home.TenantId);
        await db.SaveChangesAsync();

        return new World(
            databaseName,
            home.TenantId,
            home.ProductId,
            home.ProductName,
            home.RentalAssetId,
            foreign.TenantId,
            foreign.ProductId,
            foreign.RentalAssetId);
    }

    private static TenantSlice AddTenant(
        AppDbContext db,
        FakeTenantProvider provider,
        string legalName,
        bool catalogActive,
        string productName)
    {
        var tenant = new Tenant(legalName, Guid.NewGuid().ToString("N")[..14], subdomain: $"cat-{Guid.NewGuid():N}"[..16]);
        provider.TenantId = tenant.Id;
        var unit = new Unit(tenant.Id, "Matriz");
        var category = new AssetCategory { TenantId = tenant.Id, Name = "Equipamentos" };
        var family = new AssetFamily
        {
            Key = $"equip-{Guid.NewGuid():N}"[..32],
            Label = "Equipment",
            FieldSchemaJson = "{}",
        };
        var asset = new Asset
        {
            TenantId = tenant.Id,
            UnitId = unit.Id,
            CategoryId = category.Id,
            FamilyId = family.Id,
            Name = "Printer",
            Tag = "P1",
            Status = AssetStatus.Active,
            IsRentable = true,
        };
        var rental = new RentalAsset
        {
            TenantId = tenant.Id,
            AssetId = asset.Id,
            Type = RentalAssetType.Good,
            TotalQuantity = 1,
            IsActive = true,
            RequiresDeposit = false,
            SchedulePolicy = SchedulePolicy.OpenHours,
        };
        var product = new CatalogProduct
        {
            TenantId = tenant.Id,
            Name = productName,
            Price = 10m,
            Currency = "BRL",
            IsActive = true,
        };

        db.Tenants.Add(tenant);
        db.Units.Add(unit);
        db.AssetCategories.Add(category);
        db.AssetFamilies.Add(family);
        db.Assets.Add(asset);
        db.RentalAssets.Add(rental);
        db.CatalogProducts.Add(product);
        if (catalogActive)
        {
            db.TenantModules.Add(new TenantModule(tenant.Id, PlatformModules.Catalog, isActive: true));
        }

        return new TenantSlice(tenant.Id, product.Id, product.Name, rental.Id);
    }

    private static void AddStaff(AppDbContext db, Guid tenantId)
    {
        var manage = new Permission(
            Permissions.Catalog.ProductsManage,
            "Manage catalog products",
            moduleKey: PlatformModules.Catalog);
        var read = new Permission(
            Permissions.Catalog.ProductsRead,
            "Read catalog products",
            moduleKey: PlatformModules.Catalog);
        var editors = new Role(tenantId, "Catalog editors");
        var readers = new Role(tenantId, "Catalog readers");
        var manager = new User(tenantId, ManagerEmail, "Manager", ManagerEmail);
        var reader = new User(tenantId, ReaderEmail, "Reader", ReaderEmail);
        db.Permissions.AddRange(manage, read);
        db.Roles.AddRange(editors, readers);
        db.Users.AddRange(manager, reader);
        db.RolePermissions.Add(new RolePermission(editors.Id, manage.Id));
        db.RolePermissions.Add(new RolePermission(readers.Id, read.Id));
        db.UserRoles.Add(new UserRole(manager.Id, editors.Id));
        db.UserRoles.Add(new UserRole(reader.Id, readers.Id));
    }

    private static AppDbContext Verify(string databaseName, Guid tenantId) =>
        InMemoryAppDb.Create(new FakeTenantProvider { TenantId = tenantId }, databaseName);

    private static HttpClient Client(IHost host, string email, Guid tenantId)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, email);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, tenantId.ToString());
        return client;
    }

    private static IHost StartHost(string databaseName, bool allowPermissionDespiteModule) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddHttpContextAccessor();
                    services.AddSingleton<IPlatformAdminChecker>(_ => new FakePlatformAdminChecker());
                    services.AddScoped<AmbientTenantContext>();
                    services.AddScoped<PlatformAdminTenantSession>();
                    services.AddScoped<ITenantProvider, HttpContextTenantProvider>();
                    services.AddScoped<AppDbContext>(sp =>
                        InMemoryAppDb.Create(sp.GetRequiredService<ITenantProvider>(), databaseName));
                    services.AddScoped<ITenantModuleAccessor, TenantModuleAccessor>();
                    services.AddScoped<IPermissionResolver, PermissionResolver>();
                    services.AddSingleton<ICatalogProductService, UnusedCatalogProductService>();
                    services.AddScoped<ICatalogProductResourceRequirementService, CatalogProductResourceRequirementService>();
                    if (allowPermissionDespiteModule)
                    {
                        services.AddSingleton<IAuthorizationHandler>(
                            new AllowPermissionHandler(Permissions.Catalog.ProductsManage));
                    }
                    else
                    {
                        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
                    }

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

                            manager.ApplicationParts.Add(new AssemblyPart(typeof(CatalogProductsController).Assembly));
                            manager.FeatureProviders.Add(new SingleController(typeof(CatalogProductsController)));
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

    private sealed record World(
        string DatabaseName,
        Guid TenantId,
        Guid ProductId,
        string ProductName,
        Guid RentalAssetId,
        Guid ForeignTenantId,
        Guid ForeignProductId,
        Guid ForeignRentalAssetId);

    private sealed record TenantSlice(Guid TenantId, Guid ProductId, string ProductName, Guid RentalAssetId);

    private sealed class SingleController(Type controllerType) : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) => typeInfo.AsType() == controllerType;
    }

    private sealed class AllowPermissionHandler(string permissionKey) : AuthorizationHandler<PermissionRequirement>
    {
        protected override Task HandleRequirementAsync(
            AuthorizationHandlerContext context,
            PermissionRequirement requirement)
        {
            if (requirement.PermissionKeys.Contains(permissionKey))
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class UnusedCatalogProductService : ICatalogProductService
    {
        public Task<IReadOnlyList<CatalogProductResponse>> ListAsync(
            CatalogProductListQuery query,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CatalogProductResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
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

        public Task<CatalogProductResponse?> DeactivateAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CatalogProductResponse?> ActivateAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CatalogProductFileDto> AddFileAsync(
            Guid productId,
            string fileName,
            string contentType,
            Stream content,
            CatalogFileVisibility visibility,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteFileAsync(Guid productId, Guid fileId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CatalogFileUrlResponse> GetFileUrlAsync(
            Guid productId,
            Guid fileId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
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
            }

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
