using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Authorization;
using Platform.Api.Modules.Assets.Services;
using Platform.Api.Modules.Users.Services;
using Platform.Api.Modules.WorkOrders.Services;
using Platform.Api.Notifications;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.WorkOrders;

public sealed class WorkOrderAssignedOnlyTests
{
    [Fact]
    public async Task Execute_only_user_lists_assigned_work_orders_without_technician_name()
    {
        await using var harness = await WorkOrderRbacHarness.CreateAsync();
        var mine = harness.AddWorkOrder(harness.Executor.Id);
        harness.AddWorkOrder(harness.Creator.Id);
        await harness.Db.SaveChangesAsync();
        harness.SetUser(harness.Executor);

        var listed = await harness.Service.ListAsync(assetId: null, CancellationToken.None);

        Assert.Single(listed);
        Assert.Equal(mine.Id, listed[0].Id);
    }

    [Fact]
    public async Task Create_permission_lists_all_work_orders()
    {
        await using var harness = await WorkOrderRbacHarness.CreateAsync();
        harness.AddWorkOrder(harness.Executor.Id);
        harness.AddWorkOrder(harness.Creator.Id);
        await harness.Db.SaveChangesAsync();
        harness.SetUser(harness.Creator);

        var listed = await harness.Service.ListAsync(assetId: null, CancellationToken.None);

        Assert.Equal(2, listed.Count);
    }

    [Fact]
    public async Task Technician_role_name_alone_does_not_restrict_or_allow_assignment()
    {
        await using var harness = await WorkOrderRbacHarness.CreateAsync();
        harness.AddWorkOrder(harness.NamedTechnician.Id);
        harness.AddWorkOrder(harness.Executor.Id);
        await harness.Db.SaveChangesAsync();
        harness.SetUser(harness.NamedTechnician);

        var listed = await harness.Service.ListAsync(assetId: null, CancellationToken.None);

        Assert.Equal(2, listed.Count);
    }
}

internal sealed class WorkOrderRbacHarness : IAsyncDisposable
{
    private WorkOrderRbacHarness(
        AppDbContext db,
        FakeTenantProvider tenantProvider,
        FakeHttpContextAccessor http,
        WorkOrderService service,
        User executor,
        User creator,
        User namedTechnician,
        Guid assetId)
    {
        Db = db;
        TenantProvider = tenantProvider;
        Http = http;
        Service = service;
        Executor = executor;
        Creator = creator;
        NamedTechnician = namedTechnician;
        AssetId = assetId;
    }

    public AppDbContext Db { get; }

    public FakeTenantProvider TenantProvider { get; }

    public FakeHttpContextAccessor Http { get; }

    public WorkOrderService Service { get; }

    public User Executor { get; }

    public User Creator { get; }

    public User NamedTechnician { get; }

    public Guid AssetId { get; }

    public static async Task<WorkOrderRbacHarness> CreateAsync()
    {
        var tenant = new Tenant("OS Club", "55555555000191");
        var tenantProvider = new FakeTenantProvider { TenantId = tenant.Id };
        var db = InMemoryAppDb.Create(tenantProvider);
        db.Tenants.Add(tenant);
        db.TenantModules.Add(new TenantModule(tenant.Id, PlatformModules.WorkOrders, isActive: true));
        foreach (var entry in PermissionCatalog.All)
        {
            db.Permissions.Add(new Permission(entry.Key, entry.Name, entry.Description, entry.ModuleKey));
        }

        var unit = new Unit(tenant.Id, "Matriz");
        var family = new AssetFamily
        {
            Key = "spaces",
            Label = "Spaces",
            FieldSchemaJson = """{"fields":[]}""",
        };
        var category = new AssetCategory { TenantId = tenant.Id, Name = "Quadras" };
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
            Name = "Quadra 1",
            Tag = "Q1",
            Status = AssetStatus.Active,
        };
        db.Assets.Add(asset);

        var executeRole = new Role(tenant.Id, "Executor");
        var createRole = new Role(tenant.Id, "Creator");
        var techName = new Role(tenant.Id, SystemRoles.Technician, isSystemRole: true);
        db.Roles.AddRange(executeRole, createRole, techName);

        Permission Perm(string key) => db.Permissions.Local.First(item => item.Key == key);
        db.RolePermissions.Add(new RolePermission(executeRole.Id, Perm(Permissions.Os.WorkOrdersExecute).Id));
        db.RolePermissions.Add(new RolePermission(executeRole.Id, Perm(Permissions.Os.WorkOrdersRead).Id));
        db.RolePermissions.Add(new RolePermission(createRole.Id, Perm(Permissions.Os.WorkOrdersCreate).Id));
        db.RolePermissions.Add(new RolePermission(createRole.Id, Perm(Permissions.Os.WorkOrdersRead).Id));

        var executor = new User(tenant.Id, "exec-auth", "Executor", "exec@test.com");
        var creator = new User(tenant.Id, "create-auth", "Creator", "create@test.com");
        var named = new User(tenant.Id, "tech-auth", "Named Tech", "tech@test.com");
        db.Users.AddRange(executor, creator, named);
        db.UserRoles.Add(new UserRole(executor.Id, executeRole.Id));
        db.UserRoles.Add(new UserRole(creator.Id, createRole.Id));
        db.UserRoles.Add(new UserRole(named.Id, techName.Id));
        await db.SaveChangesAsync();

        var http = new FakeHttpContextAccessor();
        var resolver = new PermissionResolver(db, NullLogger<PermissionResolver>.Instance);
        var grantGuard = new RbacGrantGuard(db, resolver, NullLogger<RbacGrantGuard>.Instance);
        var users = new UserDirectoryService(
            db,
            tenantProvider,
            new FakePlatformAdminChecker(),
            resolver,
            grantGuard,
            new FakeTrialGuard(),
            new NotificationQueue(),
            new ConfigurationBuilder().Build(),
            new FakeHostEnvironment(),
            NullLogger<UserDirectoryService>.Instance);
        var service = new WorkOrderService(
            db,
            tenantProvider,
            http,
            users,
            resolver,
            new AssetRegistry(
                db,
                tenantProvider,
                new AssetService(db, tenantProvider, new FakeTrialGuard()),
                new AssetFamilyService(db)));

        return new WorkOrderRbacHarness(
            db,
            tenantProvider,
            http,
            service,
            executor,
            creator,
            named,
            asset.Id);
    }

    public WorkOrder AddWorkOrder(Guid? assignedUserId)
    {
        var workOrder = new WorkOrder
        {
            TenantId = TenantProvider.TenantId!.Value,
            AssetId = AssetId,
            AssignedUserId = assignedUserId,
            Status = WorkOrderStatus.Pending,
            ScheduledDate = DateOnly.FromDateTime(DateTime.UtcNow),
        };
        Db.WorkOrders.Add(workOrder);
        return workOrder;
    }

    public void SetUser(User user)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim("sub", user.SupabaseAuthId),
                new Claim("email", user.Email),
            ],
            authenticationType: "Test");
        Http.HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity),
        };
    }

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
