using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
using Platform.Api.Modules.Rentals.Contracts;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Common;
using Platform.Api.Modules.Assets;
using Platform.Api.Modules.Assets.Controllers;
using Platform.Api.Modules.Assets.Services;
using Platform.Api.Services.Trial;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Assets;

/// <summary>
/// HTTP delete for an asset or category that already has a work order.
/// The first DELETE schedules removal. The second DELETE is the permanent one.
/// There is no bulk asset or category delete route.
/// </summary>
public sealed class AssetDeleteHttpTests
{
    private const string WriterEmail = "writer@tenant.test";

    [Fact]
    public async Task Second_asset_delete_with_a_work_order_returns_409_ASSET_IN_USE()
    {
        var seed = await SeedAsync();
        using var host = StartHost(seed.DatabaseName);

        var first = await DeleteAsync(host, $"/api/assets/{seed.AssetId}", seed.TenantId);
        var firstBody = await first.Content.ReadAsStringAsync();
        Assert.True(first.StatusCode == HttpStatusCode.OK, firstBody);
        using (var firstDoc = JsonDocument.Parse(firstBody))
        {
            Assert.False(firstDoc.RootElement.GetProperty("permanentlyDeleted").GetBoolean());
        }

        var second = await DeleteAsync(host, $"/api/assets/{seed.AssetId}", seed.TenantId);
        var secondBody = await second.Content.ReadAsStringAsync();

        Assert.True(second.StatusCode == HttpStatusCode.Conflict, secondBody);
        Assert.Equal(AssetInUseException.Code, ReadCode(secondBody));
        Assert.Equal(1, await CountAssetsAsync(seed));
    }

    [Fact]
    public async Task Second_category_delete_with_a_work_order_returns_409_CATEGORY_IN_USE()
    {
        var seed = await SeedAsync();
        using var host = StartHost(seed.DatabaseName);

        var first = await DeleteAsync(host, $"/api/asset-categories/{seed.CategoryId}", seed.TenantId);
        var firstBody = await first.Content.ReadAsStringAsync();
        Assert.True(first.StatusCode == HttpStatusCode.OK, firstBody);
        using (var firstDoc = JsonDocument.Parse(firstBody))
        {
            Assert.False(firstDoc.RootElement.GetProperty("permanentlyDeleted").GetBoolean());
        }

        var second = await DeleteAsync(host, $"/api/asset-categories/{seed.CategoryId}", seed.TenantId);
        var secondBody = await second.Content.ReadAsStringAsync();

        Assert.True(second.StatusCode == HttpStatusCode.Conflict, secondBody);
        Assert.Equal(CategoryInUseException.Code, ReadCode(secondBody));
        Assert.Equal(1, await CountCategoriesAsync(seed));
    }

    private static string? ReadCode(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("code").GetString();
    }

    private static async Task<int> CountAssetsAsync(Seed seed)
    {
        var provider = new FakeTenantProvider { TenantId = seed.TenantId };
        await using var db = InMemoryAppDb.Create(provider, seed.DatabaseName);
        return await db.Assets.CountAsync(asset => asset.Id == seed.AssetId);
    }

    private static async Task<int> CountCategoriesAsync(Seed seed)
    {
        var provider = new FakeTenantProvider { TenantId = seed.TenantId };
        await using var db = InMemoryAppDb.Create(provider, seed.DatabaseName);
        return await db.AssetCategories.CountAsync(category => category.Id == seed.CategoryId);
    }

    private static async Task<HttpResponseMessage> DeleteAsync(IHost host, string path, Guid tenantId)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, WriterEmail);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, tenantId.ToString());
        return await client.DeleteAsync(path);
    }

    private static async Task<Seed> SeedAsync()
    {
        var databaseName = $"asset-delete-http-{Guid.NewGuid():N}";
        var tenant = new Tenant(
            "Asset Delete Tenant",
            Random.Shared.NextInt64(10_000_000_000_000, 99_999_999_999_999).ToString(),
            subdomain: $"asset{Guid.NewGuid():N}"[..12]);
        var provider = new FakeTenantProvider { TenantId = tenant.Id };
        await using var db = InMemoryAppDb.Create(provider, databaseName);
        db.Tenants.Add(tenant);
        db.TenantModules.Add(new TenantModule(tenant.Id, PlatformModules.Inventory, isActive: true));

        var assetsWrite = new Permission(
            Permissions.Inventory.AssetsWrite,
            "Write assets",
            moduleKey: PlatformModules.Inventory);
        var categoriesWrite = new Permission(
            Permissions.Inventory.CategoriesWrite,
            "Write categories",
            moduleKey: PlatformModules.Inventory);
        db.Permissions.AddRange(assetsWrite, categoriesWrite);
        var role = new Role(tenant.Id, "Writer");
        db.Roles.Add(role);
        db.RolePermissions.Add(new RolePermission(role.Id, assetsWrite.Id));
        db.RolePermissions.Add(new RolePermission(role.Id, categoriesWrite.Id));

        var user = new User(tenant.Id, WriterEmail, "Writer", WriterEmail);
        db.Users.Add(user);
        db.UserRoles.Add(new UserRole(user.Id, role.Id));

        var unit = new Unit(tenant.Id, "Main");
        var family = new AssetFamily
        {
            Key = $"equipment-{Guid.NewGuid():N}"[..24],
            Label = "Equipment",
            FieldSchemaJson = """{"fields":[]}""",
        };
        var category = new AssetCategory { TenantId = tenant.Id, Name = "Equipment" };
        db.Units.Add(unit);
        db.AssetFamilies.Add(family);
        db.AssetCategories.Add(category);
        await db.SaveChangesAsync();

        var asset = new Asset
        {
            TenantId = tenant.Id,
            UnitId = unit.Id,
            CategoryId = category.Id,
            FamilyId = family.Id,
            Name = "Asset",
            Tag = "A-1",
            Status = AssetStatus.Active,
        };
        db.Assets.Add(asset);
        db.WorkOrders.Add(new WorkOrder
        {
            TenantId = tenant.Id,
            AssetId = asset.Id,
            Status = WorkOrderStatus.Pending,
            ScheduledDate = new DateOnly(2026, 10, 17),
        });
        await db.SaveChangesAsync();

        return new Seed(databaseName, tenant.Id, asset.Id, category.Id);
    }

    private static IHost StartHost(string databaseName) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddHttpContextAccessor();
                    services.AddSingleton<IPlatformAdminChecker>(_ => new FakePlatformAdminChecker());
                    services.AddSingleton<ITrialGuard, FakeTrialGuard>();
                    services.AddScoped<AmbientTenantContext>();
                    services.AddScoped<PlatformAdminTenantSession>();
                    services.AddScoped<ITenantProvider, HttpContextTenantProvider>();
                    services.AddScoped<AppDbContext>(sp =>
                        InMemoryAppDb.Create(sp.GetRequiredService<ITenantProvider>(), databaseName));
                    services.AddScoped<ITenantModuleAccessor, TenantModuleAccessor>();
                    services.AddScoped<IPermissionResolver, PermissionResolver>();
                    services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
                    services.AddScoped<IAssetCategoryService, AssetCategoryService>();
                    services.AddScoped<IAssetService, AssetService>();
                    services.AddScoped<IRentalConfigurationSync, RentalConfigurationSync>();
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

                            manager.ApplicationParts.Add(new AssemblyPart(typeof(AssetsController).Assembly));
                            manager.FeatureProviders.Add(new SelectedControllers(
                                typeof(AssetsController),
                                typeof(AssetCategoriesController)));
                        });
                });
                web.Configure(app =>
                {
                    app.UseExceptionHandler(errorApp =>
                    {
                        errorApp.Run(async context =>
                        {
                            var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
                            context.Response.StatusCode = exception is UnauthorizedAccessException or TenantResolutionException
                                ? StatusCodes.Status401Unauthorized
                                : StatusCodes.Status500InternalServerError;
                            await context.Response.WriteAsJsonAsync(new
                            {
                                error = exception?.ToString() ?? "An unexpected error occurred.",
                            });
                        });
                    });
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .Start();

    private sealed record Seed(string DatabaseName, Guid TenantId, Guid AssetId, Guid CategoryId);

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
            }

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
