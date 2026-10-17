using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Common;
using Platform.Api.Modules.Assets.Services;
using Platform.Api.Modules.Users.Services;
using Platform.Api.Modules.WorkOrders;
using Platform.Api.Modules.WorkOrders.Controllers;
using Platform.Api.Modules.WorkOrders.Services;
using Platform.Api.Notifications;
using Platform.Api.Services.Trial;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.WorkOrders;

/// <summary>
/// HTTP complete-then-write for the only task-value route the web calls.
/// vlr-web develop saves a task with <c>PATCH /api/work-orders/{id}/tasks/{taskId}</c>
/// from both the online update and <c>src/lib/offlineSync.ts</c>. There is no bulk route.
/// </summary>
public sealed class WorkOrderClosedWriteHttpTests
{
    private const string ExecutorEmail = "executor@tenant.test";

    [Fact]
    public async Task Complete_then_task_patch_returns_409_and_keeps_the_stored_value()
    {
        var seed = await SeedAsync();
        using var host = StartHost(seed.DatabaseName);

        var complete = await SendAsync(
            host,
            HttpMethod.Patch,
            $"/api/work-orders/{seed.WorkOrderId}/status",
            """{"status":"Completed"}""",
            seed.TenantId);
        var completeBody = await complete.Content.ReadAsStringAsync();
        Assert.True(complete.StatusCode == HttpStatusCode.OK, completeBody);

        var write = await SendAsync(
            host,
            HttpMethod.Patch,
            $"/api/work-orders/{seed.WorkOrderId}/tasks/{seed.TaskId}",
            """{"value":"queued"}""",
            seed.TenantId);
        var writeBody = await write.Content.ReadAsStringAsync();

        Assert.True(write.StatusCode == HttpStatusCode.Conflict, writeBody);
        Assert.Equal(WorkOrderErrorCodes.Closed, ReadCode(writeBody));
        Assert.Null(await ReadValueAsync(seed));
    }

    [Fact]
    public async Task Task_patch_before_completion_is_kept_on_the_completed_order()
    {
        var seed = await SeedAsync();
        using var host = StartHost(seed.DatabaseName);

        var write = await SendAsync(
            host,
            HttpMethod.Patch,
            $"/api/work-orders/{seed.WorkOrderId}/tasks/{seed.TaskId}",
            """{"value":"before"}""",
            seed.TenantId);
        var writeBody = await write.Content.ReadAsStringAsync();
        Assert.True(write.StatusCode == HttpStatusCode.OK, writeBody);

        var complete = await SendAsync(
            host,
            HttpMethod.Patch,
            $"/api/work-orders/{seed.WorkOrderId}/status",
            """{"status":"Completed"}""",
            seed.TenantId);
        var completeBody = await complete.Content.ReadAsStringAsync();

        Assert.True(complete.StatusCode == HttpStatusCode.OK, completeBody);
        Assert.Equal("before", await ReadValueAsync(seed));
        Assert.Equal(WorkOrderStatus.Completed, await ReadStatusAsync(seed));
    }

    [Fact]
    public async Task Cancel_then_task_patch_returns_409_and_keeps_the_stored_value()
    {
        var seed = await SeedAsync();
        using var host = StartHost(seed.DatabaseName);

        var cancel = await SendAsync(
            host,
            HttpMethod.Post,
            $"/api/work-orders/{seed.WorkOrderId}/cancel",
            "{}",
            seed.TenantId);
        var cancelBody = await cancel.Content.ReadAsStringAsync();
        Assert.True(cancel.StatusCode == HttpStatusCode.OK, cancelBody);

        var write = await SendAsync(
            host,
            HttpMethod.Patch,
            $"/api/work-orders/{seed.WorkOrderId}/tasks/{seed.TaskId}",
            """{"value":"queued"}""",
            seed.TenantId);
        var writeBody = await write.Content.ReadAsStringAsync();

        Assert.True(write.StatusCode == HttpStatusCode.Conflict, writeBody);
        Assert.Equal(WorkOrderErrorCodes.Closed, ReadCode(writeBody));
        Assert.Null(await ReadValueAsync(seed));
    }

    private static string? ReadCode(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("code").GetString();
    }

    private static async Task<string?> ReadValueAsync(Seed seed)
    {
        var provider = new FakeTenantProvider { TenantId = seed.TenantId };
        await using var db = InMemoryAppDb.Create(provider, seed.DatabaseName);
        return await db.WorkOrderTasks
            .Where(task => task.Id == seed.TaskId)
            .Select(task => task.Value)
            .SingleAsync();
    }

    private static async Task<WorkOrderStatus> ReadStatusAsync(Seed seed)
    {
        var provider = new FakeTenantProvider { TenantId = seed.TenantId };
        await using var db = InMemoryAppDb.Create(provider, seed.DatabaseName);
        return await db.WorkOrders
            .Where(workOrder => workOrder.Id == seed.WorkOrderId)
            .Select(workOrder => workOrder.Status)
            .SingleAsync();
    }

    private static async Task<HttpResponseMessage> SendAsync(
        IHost host,
        HttpMethod method,
        string path,
        string json,
        Guid tenantId)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, ExecutorEmail);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, tenantId.ToString());
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        return await client.SendAsync(request);
    }

    private static async Task<Seed> SeedAsync()
    {
        var databaseName = $"wo-closed-http-{Guid.NewGuid():N}";
        var tenant = new Tenant(
            "Closed Write Tenant",
            Random.Shared.NextInt64(10_000_000_000_000, 99_999_999_999_999).ToString(),
            subdomain: $"closed{Guid.NewGuid():N}"[..12]);
        var provider = new FakeTenantProvider { TenantId = tenant.Id };
        await using var db = InMemoryAppDb.Create(provider, databaseName);
        db.Tenants.Add(tenant);
        db.TenantModules.Add(new TenantModule(tenant.Id, PlatformModules.WorkOrders, isActive: true));

        var execute = new Permission(
            Permissions.Os.WorkOrdersExecute,
            "Execute work orders",
            moduleKey: PlatformModules.WorkOrders);
        var create = new Permission(
            Permissions.Os.WorkOrdersCreate,
            "Create work orders",
            moduleKey: PlatformModules.WorkOrders);
        db.Permissions.AddRange(execute, create);
        var role = new Role(tenant.Id, "Executor");
        db.Roles.Add(role);
        db.RolePermissions.Add(new RolePermission(role.Id, execute.Id));
        db.RolePermissions.Add(new RolePermission(role.Id, create.Id));

        var user = new User(tenant.Id, ExecutorEmail, "Executor", ExecutorEmail);
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
        var workOrder = new WorkOrder
        {
            TenantId = tenant.Id,
            AssetId = asset.Id,
            AssignedUserId = user.Id,
            Status = WorkOrderStatus.Pending,
            ScheduledDate = new DateOnly(2026, 10, 17),
        };
        var task = new WorkOrderTask
        {
            TenantId = tenant.Id,
            WorkOrderId = workOrder.Id,
            Title = "Reading",
            InputType = TaskInputType.Text,
            IsMandatory = false,
            Order = 1,
        };
        task.WorkOrder = workOrder;
        workOrder.AddTask(task);
        db.WorkOrders.Add(workOrder);
        await db.SaveChangesAsync();

        return new Seed(databaseName, tenant.Id, workOrder.Id, task.Id);
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
                    services.AddSingleton<IConfiguration>(_ => new ConfigurationBuilder().Build());
                    services.AddSingleton<IHostEnvironment, FakeHostEnvironment>();
                    services.AddSingleton<IPlatformAdminChecker>(_ => new FakePlatformAdminChecker());
                    services.AddSingleton<ITrialGuard, FakeTrialGuard>();
                    services.AddSingleton<NotificationQueue>();
                    services.AddScoped<AmbientTenantContext>();
                    services.AddScoped<PlatformAdminTenantSession>();
                    services.AddScoped<ITenantProvider, HttpContextTenantProvider>();
                    services.AddScoped<AppDbContext>(sp =>
                        InMemoryAppDb.Create(sp.GetRequiredService<ITenantProvider>(), databaseName));
                    services.AddScoped<ITenantModuleAccessor, TenantModuleAccessor>();
                    services.AddScoped<IPermissionResolver, PermissionResolver>();
                    services.AddScoped<IRbacGrantGuard, RbacGrantGuard>();
                    services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
                    services.AddScoped<IAssetFamilyService, AssetFamilyService>();
                    services.AddScoped<IAssetService, AssetService>();
                    services.AddScoped<IAssetRegistry, AssetRegistry>();
                    services.AddScoped<IUserDirectoryService, UserDirectoryService>();
                    services.AddScoped<IWorkOrderGenerationService, WorkOrderGenerationService>();
                    services.AddScoped<IWorkOrderService, WorkOrderService>();
                    services.AddAuthorization(options => options.AddRolvixPolicies());
                    services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
                    services.AddSingleton<IAuthorizationMiddlewareResultHandler, JsonForbiddenAuthorizationResultHandler>();
                    services
                        .AddAuthentication(SupabaseJwtBearerDefaults.AuthenticationScheme)
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                            SupabaseJwtBearerDefaults.AuthenticationScheme,
                            _ => { });
                    services.AddControllers()
                        .AddJsonOptions(options =>
                            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
                        .ConfigureApplicationPartManager(manager =>
                        {
                            manager.ApplicationParts.Clear();
                            foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
                            {
                                manager.FeatureProviders.Remove(provider);
                            }

                            manager.ApplicationParts.Add(new AssemblyPart(typeof(WorkOrdersController).Assembly));
                            manager.FeatureProviders.Add(new SelectedControllers(typeof(WorkOrdersController)));
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

    private sealed record Seed(string DatabaseName, Guid TenantId, Guid WorkOrderId, Guid TaskId);

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
