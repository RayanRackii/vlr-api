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
using Platform.Api.Common;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Modules.Assets.Services;
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
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.WorkOrders;

/// <summary>
/// HTTP authorization for cancel, assignee, and technician lookup.
/// Unauthenticated callers receive 401. An authenticated caller who lacks the
/// action's permission receives 403 FORBIDDEN. The actions are not invoked
/// for those responses.
/// </summary>
public sealed class WorkOrderEndpointAuthorizationTests
{
    private const string ReaderEmail = "reader@tenant.test";
    private const string CreatorEmail = "creator@tenant.test";
    private const string AssignerEmail = "assigner@tenant.test";
    private const string DirectoryEmail = "directory@tenant.test";
    private static readonly Guid WorkOrderId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task Cancel_assignee_and_technicians_without_authentication_return_unauthorized()
    {
        var seed = await SeedAsync();
        using var host = StartHost(seed.DatabaseName);
        var client = host.GetTestClient();

        var cancel = await client.PostAsync(CancelPath(), EmptyJson());
        var assignee = await client.PutAsync(AssigneePath(), EmptyJson());
        var technicians = await client.GetAsync("/api/users/technicians");

        Assert.Equal(HttpStatusCode.Unauthorized, cancel.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, assignee.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, technicians.StatusCode);
    }

    [Fact]
    public async Task Cancel_assignee_and_technicians_without_permission_return_forbidden()
    {
        var seed = await SeedAsync();
        using var host = StartHost(seed.DatabaseName);

        var cancel = await SendAsync(host, HttpMethod.Post, CancelPath(), ReaderEmail, seed.TenantId);
        var assignee = await SendAsync(host, HttpMethod.Put, AssigneePath(), ReaderEmail, seed.TenantId);
        var technicians = await SendAsync(host, HttpMethod.Get, "/api/users/technicians", ReaderEmail, seed.TenantId);

        Assert.Equal(HttpStatusCode.Forbidden, cancel.StatusCode);
        Assert.Equal(RbacErrorCodes.Forbidden, await ReadErrorAsync(cancel));
        Assert.Equal(HttpStatusCode.Forbidden, assignee.StatusCode);
        Assert.Equal(RbacErrorCodes.Forbidden, await ReadErrorAsync(assignee));
        Assert.Equal(HttpStatusCode.Forbidden, technicians.StatusCode);
        Assert.Equal(RbacErrorCodes.Forbidden, await ReadErrorAsync(technicians));
    }

    [Fact]
    public async Task Permitted_callers_reach_cancel_assignee_and_technicians()
    {
        var seed = await SeedAsync();
        using var host = StartHost(seed.DatabaseName);

        var cancel = await SendAsync(host, HttpMethod.Post, CancelPath(), CreatorEmail, seed.TenantId);
        var assignee = await SendAsync(host, HttpMethod.Put, AssigneePath(), AssignerEmail, seed.TenantId);
        var byDirectory = await SendAsync(host, HttpMethod.Get, "/api/users/technicians", DirectoryEmail, seed.TenantId);
        var byCreate = await SendAsync(host, HttpMethod.Get, "/api/users/technicians", CreatorEmail, seed.TenantId);
        var byAssign = await SendAsync(host, HttpMethod.Get, "/api/users/technicians", AssignerEmail, seed.TenantId);

        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, assignee.StatusCode);
        Assert.Equal(HttpStatusCode.OK, byDirectory.StatusCode);
        Assert.Equal(HttpStatusCode.OK, byCreate.StatusCode);
        Assert.Equal(HttpStatusCode.OK, byAssign.StatusCode);
    }

    private static string CancelPath() => $"/api/work-orders/{WorkOrderId}/cancel";

    private static string AssigneePath() => $"/api/work-orders/{WorkOrderId}/assignee";

    private static StringContent EmptyJson() =>
        new("{\"assignedUserId\":null}", Encoding.UTF8, "application/json");

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("error").GetString();
    }

    private static Task<HttpResponseMessage> SendAsync(
        IHost host,
        HttpMethod method,
        string path,
        string email,
        Guid tenantId)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, email);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, tenantId.ToString());
        var request = new HttpRequestMessage(method, path);
        if (method != HttpMethod.Get)
        {
            request.Content = EmptyJson();
        }

        return client.SendAsync(request);
    }

    private static async Task<Seed> SeedAsync()
    {
        var databaseName = $"wo-auth-{Guid.NewGuid():N}";
        var tenant = new Tenant("Work Order Tenant", "33333333000191", subdomain: "woauth");
        var provider = new FakeTenantProvider { TenantId = tenant.Id };
        await using var db = InMemoryAppDb.Create(provider, databaseName);
        db.Tenants.Add(tenant);
        db.TenantModules.Add(new TenantModule(tenant.Id, PlatformModules.WorkOrders, isActive: true));

        var read = Permission(Permissions.Os.WorkOrdersRead, "Read work orders");
        var create = Permission(Permissions.Os.WorkOrdersCreate, "Create work orders");
        var assign = Permission(Permissions.Os.WorkOrdersAssign, "Assign work orders");
        var usersRead = Permission(Permissions.Core.UsersRead, "Read users");
        db.Permissions.AddRange(read, create, assign, usersRead);

        var readerRole = new Role(tenant.Id, "Reader", "Reader");
        var creatorRole = new Role(tenant.Id, "Creator", "Creator");
        var assignerRole = new Role(tenant.Id, "Assigner", "Assigner");
        var directoryRole = new Role(tenant.Id, "Directory", "Directory");
        db.Roles.AddRange(readerRole, creatorRole, assignerRole, directoryRole);
        db.RolePermissions.AddRange(
            new RolePermission(readerRole.Id, read.Id),
            new RolePermission(creatorRole.Id, create.Id),
            new RolePermission(assignerRole.Id, assign.Id),
            new RolePermission(directoryRole.Id, usersRead.Id));

        var reader = User(tenant.Id, ReaderEmail);
        var creator = User(tenant.Id, CreatorEmail);
        var assigner = User(tenant.Id, AssignerEmail);
        var directory = User(tenant.Id, DirectoryEmail);
        db.Users.AddRange(reader, creator, assigner, directory);
        db.UserRoles.AddRange(
            new UserRole(reader.Id, readerRole.Id),
            new UserRole(creator.Id, creatorRole.Id),
            new UserRole(assigner.Id, assignerRole.Id),
            new UserRole(directory.Id, directoryRole.Id));
        await db.SaveChangesAsync();

        return new Seed(databaseName, tenant.Id);
    }

    private static Permission Permission(string key, string name) =>
        new(key, name, moduleKey: key.StartsWith("os.", StringComparison.Ordinal)
            ? PlatformModules.WorkOrders
            : null);

    private static User User(Guid tenantId, string email) =>
        new(tenantId, email, email, email);

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
                    services.AddSingleton<IWorkOrderService, StubWorkOrderService>();
                    services.AddSingleton<IWorkOrderGenerationService, StubGenerationService>();
                    services.AddSingleton<IAssetRegistry, StubAssetRegistry>();
                    services.AddSingleton<IUserDirectoryService, StubUserDirectory>();
                    services.AddSingleton<IRbacActorAccessor, StubActorAccessor>();
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
                            manager.FeatureProviders.Add(new SelectedControllers(
                                typeof(WorkOrdersController),
                                typeof(UsersController)));
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
                                error = exception?.Message ?? "An unexpected error occurred.",
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

    private sealed record Seed(string DatabaseName, Guid TenantId);

    private sealed class SelectedControllers(params Type[] controllerTypes) : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) =>
            controllerTypes.Contains(typeInfo.AsType());
    }

    private sealed class StubGenerationService : IWorkOrderGenerationService
    {
        public Task<WorkOrderResponse> GenerateAsync(
            GenerateWorkOrderCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PmocAutomaticGenerationResult> TryGenerateAutomaticAsync(
            Guid tenantId,
            Guid planId,
            Guid assetId,
            DateOnly asOfDate,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubWorkOrderService : IWorkOrderService
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
            Task.FromResult<WorkOrderResponse?>(null);

        public Task<WorkOrderResponse?> UpdateAssigneeAsync(
            Guid id,
            Guid? assignedUserId,
            CancellationToken cancellationToken) =>
            Task.FromResult<WorkOrderResponse?>(null);
    }

    private sealed class StubAssetRegistry : IAssetRegistry
    {
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

    private sealed class StubUserDirectory : IUserDirectoryService
    {
        public Task<CurrentUserResponse> GetCurrentAsync(
            ClaimsPrincipal principal,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CurrentUserResponse> UpdateCurrentPhoneAsync(
            ClaimsPrincipal principal,
            string? phone,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TechnicianUserResponse>> ListTechniciansAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TechnicianUserResponse>>([]);

        public Task<PagedResult<TenantMemberResponse>> ListAsync(
            DirectoryPage page,
            CancellationToken cancellationToken) =>
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

    private sealed class StubActorAccessor : IRbacActorAccessor
    {
        public Task<RbacActor> GetAsync(ClaimsPrincipal principal, CancellationToken cancellationToken) =>
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
