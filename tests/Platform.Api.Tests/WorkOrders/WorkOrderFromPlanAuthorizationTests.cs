using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Modules.Assets.Services;
using Platform.Api.Modules.WorkOrders.Controllers;
using Platform.Api.Modules.WorkOrders.Dtos;
using Platform.Api.Modules.WorkOrders.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.WorkOrders;

/// <summary>
/// HTTP contract for POST /api/work-orders/from-plan.
/// An authenticated caller with no tenant context fails the
/// <c>[RequirePermission]</c> requirement and returns 403 FORBIDDEN, the same
/// fail-closed status as a caller who lacks <c>os.work_orders.create</c> and
/// the same contract <c>GET /api/users</c> already uses. Unauthenticated
/// callers receive 401. The action is not reached, so
/// <c>GenerateAsync</c>'s <c>UnauthorizedAccessException</c> does not decide
/// this status. <c>DirectoryAuthorization.RequireTenant</c> is a different
/// surface and stays out of this route.
/// </summary>
public sealed class WorkOrderFromPlanAuthorizationTests
{
    private const string ReaderEmail = "reader@club.test";
    private const string CreatorEmail = "creator@club.test";

    [Fact]
    public async Task From_plan_without_authentication_returns_unauthorized()
    {
        var seed = await SeedAsync();
        using var host = StartHost(seed.DatabaseName);
        var client = host.GetTestClient();
        var payload = """
            {"planId":"11111111-1111-1111-1111-111111111111","assetId":"22222222-2222-2222-2222-222222222222","scheduledDate":"2026-09-18"}
            """;
        var response = await client.PostAsync(
            "/api/work-orders/from-plan",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task From_plan_without_tenant_context_returns_forbidden()
    {
        var seed = await SeedAsync();
        using var host = StartHost(seed.DatabaseName);
        var response = await PostFromPlanAsync(host, ReaderEmail, tenantId: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(RbacErrorCodes.Forbidden, await ReadErrorAsync(response));
    }

    [Fact]
    public async Task From_plan_without_create_permission_returns_forbidden()
    {
        var seed = await SeedAsync();
        using var host = StartHost(seed.DatabaseName);
        var response = await PostFromPlanAsync(host, ReaderEmail, seed.TenantId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(RbacErrorCodes.Forbidden, await ReadErrorAsync(response));
    }

    [Fact]
    public async Task From_plan_with_create_permission_passes_authorization()
    {
        var seed = await SeedAsync();
        using var host = StartHost(seed.DatabaseName);
        var response = await PostFromPlanAsync(host, CreatorEmail, seed.TenantId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("plan missing", await ReadErrorAsync(response));
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("error").GetString();
    }

    private static Task<HttpResponseMessage> PostFromPlanAsync(
        IHost host,
        string email,
        Guid? tenantId)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, email);
        if (tenantId is Guid id)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, id.ToString());
        }

        var payload = """
            {"planId":"11111111-1111-1111-1111-111111111111","assetId":"22222222-2222-2222-2222-222222222222","scheduledDate":"2026-09-18"}
            """;
        return client.PostAsync(
            "/api/work-orders/from-plan",
            new StringContent(payload, Encoding.UTF8, "application/json"));
    }

    private static async Task<Seed> SeedAsync()
    {
        var databaseName = $"wo-from-plan-{Guid.NewGuid():N}";
        var tenant = new Tenant("OS Club", "33333333000191", subdomain: "osclub");
        var provider = new FakeTenantProvider { TenantId = tenant.Id };
        await using var db = InMemoryAppDb.Create(provider, databaseName);
        db.Tenants.Add(tenant);
        db.TenantModules.Add(new TenantModule(tenant.Id, PlatformModules.WorkOrders, isActive: true));

        var read = new Permission(
            Permissions.Os.WorkOrdersRead,
            "Read work orders",
            moduleKey: PlatformModules.WorkOrders);
        var create = new Permission(
            Permissions.Os.WorkOrdersCreate,
            "Create work orders",
            moduleKey: PlatformModules.WorkOrders);
        db.Permissions.AddRange(read, create);

        var readerRole = new Role(tenant.Id, "Reader", "Reads work orders");
        var creatorRole = new Role(tenant.Id, "Creator", "Creates work orders");
        db.Roles.AddRange(readerRole, creatorRole);
        db.RolePermissions.Add(new RolePermission(readerRole.Id, read.Id));
        db.RolePermissions.Add(new RolePermission(creatorRole.Id, create.Id));

        var reader = new User(tenant.Id, ReaderEmail, "Reader", ReaderEmail);
        var creator = new User(tenant.Id, CreatorEmail, "Creator", CreatorEmail);
        db.Users.AddRange(reader, creator);
        db.UserRoles.Add(new UserRole(reader.Id, readerRole.Id));
        db.UserRoles.Add(new UserRole(creator.Id, creatorRole.Id));
        await db.SaveChangesAsync();

        return new Seed(databaseName, tenant.Id);
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
                    services.AddScoped<AmbientTenantContext>();
                    services.AddScoped<PlatformAdminTenantSession>();
                    services.AddScoped<ITenantProvider, HttpContextTenantProvider>();
                    services.AddScoped<AppDbContext>(sp =>
                        InMemoryAppDb.Create(sp.GetRequiredService<ITenantProvider>(), databaseName));
                    services.AddScoped<ITenantModuleAccessor, TenantModuleAccessor>();
                    services.AddScoped<IPermissionResolver, PermissionResolver>();
                    services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
                    services.AddSingleton<IWorkOrderService, UnusedWorkOrderService>();
                    services.AddSingleton<IWorkOrderGenerationService, PlanMissingGenerationService>();
                    services.AddSingleton<IAssetRegistry, UnusedAssetRegistry>();
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

                            manager.ApplicationParts.Add(new AssemblyPart(typeof(WorkOrdersController).Assembly));
                            manager.FeatureProviders.Add(new SingleController(typeof(WorkOrdersController)));
                        });
                });
                web.Configure(app =>
                {
                    app.UseExceptionHandler(errorApp =>
                    {
                        errorApp.Run(async context =>
                        {
                            var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
                            if (exception is TenantResolutionException tenantResolutionException)
                            {
                                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                                await context.Response.WriteAsJsonAsync(new { error = tenantResolutionException.Message });
                                return;
                            }

                            if (exception is UnauthorizedAccessException unauthorizedAccessException)
                            {
                                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                                await context.Response.WriteAsJsonAsync(new { error = unauthorizedAccessException.Message });
                                return;
                            }

                            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                            await context.Response.WriteAsJsonAsync(new { error = "An unexpected error occurred." });
                        });
                    });
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .Start();

    private sealed record Seed(string DatabaseName, Guid TenantId);

    private sealed class SingleController(Type controllerType) : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) =>
            typeInfo.AsType() == controllerType;
    }

    private sealed class PlanMissingGenerationService : IWorkOrderGenerationService
    {
        public Task<WorkOrderResponse> GenerateAsync(
            GenerateWorkOrderCommand command,
            CancellationToken cancellationToken) =>
            throw new ArgumentException("plan missing");

        public Task<PmocAutomaticGenerationResult> TryGenerateAutomaticAsync(
            Guid tenantId,
            Guid planId,
            Guid assetId,
            DateOnly asOfDate,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedWorkOrderService : IWorkOrderService
    {
        public Task<IReadOnlyList<WorkOrderResponse>> ListAsync(
            Guid? assetId,
            Guid? maintenancePlanId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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

        public Task<WorkOrderResponse?> CancelAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkOrderResponse?> UpdateAssigneeAsync(
            Guid id,
            Guid? assignedUserId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedAssetRegistry : IAssetRegistry
    {
        public Task<RegistryCategoryListItem> CreateCategoryAsync(
            CreateAssetCategoryRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RegistryCategoryListItem>> ListCategoriesAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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
