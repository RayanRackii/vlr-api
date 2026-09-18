using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Platform.Api.Authorization;
using Platform.Api.Modules.Assets.Services;
using Platform.Api.Modules.Pmoc.Dtos;
using Platform.Api.Modules.Pmoc.Services;
using Platform.Api.Modules.Users.Services;
using Platform.Api.Modules.WorkOrders;
using Platform.Api.Modules.WorkOrders.Controllers;
using Platform.Api.Modules.WorkOrders.Dtos;
using Platform.Api.Modules.WorkOrders.Services;
using Platform.Api.Notifications;
using Platform.Api.Tests.Assets;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.WorkOrders;

public sealed class WorkOrderGenerationServiceTests
{
    [Fact]
    public async Task Manual_from_plan_with_auto_generate_off_still_creates_when_active()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await CreatePlanAsync(
            harness,
            "Manual auto-off",
            autoGenerateEnabled: false);
        var asset = await CreateMatchingAssetAsync(harness);
        var scheduled = new DateOnly(2026, 9, 18);

        var created = await CreateGenerator(harness).GenerateAsync(
            new GenerateWorkOrderCommand(plan.Id, asset.Id, scheduled, AssignedUserId: null),
            CancellationToken.None);

        Assert.Equal(WorkOrderStatus.Pending, created.Status);
        Assert.Equal(plan.Id, created.MaintenancePlanId);
        Assert.Equal("Manual auto-off", created.SourcePlanName);
        Assert.Null(created.AssignedUserId);
        Assert.Null(created.Notes);
        Assert.Equal(scheduled, created.ScheduledDate);
        Assert.Equal(plan.Tasks[0].Id, created.Tasks[0].PlanTaskId);
        Assert.Equal("Filtro", created.Tasks[0].Title);
        Assert.Null(created.Tasks[0].Value);
        Assert.Single(await harness.Db.WorkOrders.ToListAsync());
    }

    [Fact]
    public async Task Source_plan_name_is_snapshotted_at_generation()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plans = CreatePlanService(harness);
        var plan = await CreatePlanAsync(harness, "Name A");
        var asset = await CreateMatchingAssetAsync(harness);
        var generator = CreateGenerator(harness);

        var first = await generator.GenerateAsync(
            new GenerateWorkOrderCommand(plan.Id, asset.Id, new DateOnly(2026, 9, 18), null),
            CancellationToken.None);

        await plans.UpdateAsync(
            plan.Id,
            HeaderUpdate(harness, "Name B", isActive: true, autoGenerateEnabled: false),
            CancellationToken.None);

        var second = await generator.GenerateAsync(
            new GenerateWorkOrderCommand(plan.Id, asset.Id, new DateOnly(2026, 9, 19), null),
            CancellationToken.None);

        Assert.Equal("Name A", first.SourcePlanName);
        Assert.Equal("Name A", (await harness.Db.WorkOrders.SingleAsync(item => item.Id == first.Id)).SourcePlanName);
        Assert.Equal("Name B", second.SourcePlanName);
    }

    [Fact]
    public async Task Checklist_snapshot_ignores_later_plan_task_replace()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plans = CreatePlanService(harness);
        var plan = await CreatePlanAsync(
            harness,
            "Checklist",
            autoGenerateEnabled: false,
            isActive: true,
            new CreatePlanTaskDto
            {
                Title = "Old title",
                InputType = TaskInputType.Checkbox,
                Order = 1,
                Configuration = """{"min":1}""",
            });
        var asset = await CreateMatchingAssetAsync(harness);
        var generator = CreateGenerator(harness);

        var first = await generator.GenerateAsync(
            new GenerateWorkOrderCommand(plan.Id, asset.Id, new DateOnly(2026, 9, 18), null),
            CancellationToken.None);

        await plans.ReplaceTasksAsync(
            plan.Id,
            new ReplacePlanTasksRequest
            {
                Tasks =
                [
                    new ReplacePlanTaskDto
                    {
                        Title = "Brand new",
                        InputType = TaskInputType.Text,
                        Order = 1,
                    },
                ],
            },
            CancellationToken.None);

        var storedFirst = await harness.Db.WorkOrderTasks.SingleAsync(task => task.WorkOrderId == first.Id);
        Assert.Equal("Old title", storedFirst.Title);
        Assert.Equal(TaskInputType.Checkbox, storedFirst.InputType);
        Assert.Equal("""{"min":1}""", storedFirst.Configuration);

        var second = await generator.GenerateAsync(
            new GenerateWorkOrderCommand(plan.Id, asset.Id, new DateOnly(2026, 9, 19), null),
            CancellationToken.None);

        Assert.Equal("Brand new", second.Tasks.Single().Title);
        Assert.Equal(TaskInputType.Text, second.Tasks.Single().InputType);
        Assert.Equal("Old title", (await harness.Db.WorkOrderTasks.SingleAsync(task => task.WorkOrderId == first.Id)).Title);
    }

    [Fact]
    public async Task Sequential_duplicate_throws_duplicate_work_order_and_keeps_one_non_canceled()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await CreatePlanAsync(harness, "Dup");
        var asset = await CreateMatchingAssetAsync(harness);
        var command = new GenerateWorkOrderCommand(plan.Id, asset.Id, new DateOnly(2026, 9, 18), null);
        var generator = CreateGenerator(harness);

        await generator.GenerateAsync(command, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<DuplicateWorkOrderException>(
            () => generator.GenerateAsync(command, CancellationToken.None));

        Assert.Equal("DUPLICATE_WORK_ORDER", DuplicateWorkOrderException.Code);
        Assert.Contains("already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await harness.Db.WorkOrders.CountAsync(item => item.Status != WorkOrderStatus.Canceled));
    }

    [Fact]
    public async Task Controller_maps_duplicate_to_409()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await CreatePlanAsync(harness, "Http-dup");
        var asset = await CreateMatchingAssetAsync(harness);
        var generator = CreateGenerator(harness);
        var request = new GenerateWorkOrderFromPlanRequest
        {
            PlanId = plan.Id,
            AssetId = asset.Id,
            ScheduledDate = new DateOnly(2026, 9, 18),
        };
        var controller = CreateController(harness, generator);

        var first = await controller.CreateFromPlan(request, CancellationToken.None);
        var created = Assert.IsType<CreatedAtActionResult>(first.Result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        Assert.Equal(nameof(WorkOrdersController.GetById), created.ActionName);

        var second = await controller.CreateFromPlan(request, CancellationToken.None);
        var conflict = Assert.IsType<ConflictObjectResult>(second.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(conflict.Value));
        Assert.Equal("DUPLICATE_WORK_ORDER", doc.RootElement.GetProperty("code").GetString());
        Assert.Contains(
            "already exists",
            doc.RootElement.GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await harness.Db.WorkOrders.CountAsync());
    }

    [Fact]
    public void Unique_violation_mapper_only_maps_pmoc_period_index()
    {
        var period = new DbUpdateException(
            "conflict",
            new PostgresException(
                "duplicate key value violates unique constraint \"ux_os_work_orders_pmoc_period\"",
                "ERROR",
                "ERROR",
                PostgresErrorCodes.UniqueViolation));
        Assert.True(WorkOrderGenerationService.IsPmocPeriodUniqueViolation(period));

        var unrelatedUnique = new DbUpdateException(
            "conflict",
            new PostgresException(
                "duplicate key value violates unique constraint \"ux_users_email\"",
                "ERROR",
                "ERROR",
                PostgresErrorCodes.UniqueViolation));
        Assert.False(WorkOrderGenerationService.IsPmocPeriodUniqueViolation(unrelatedUnique));

        var fk = new DbUpdateException(
            "conflict",
            new PostgresException(
                "violates foreign key constraint \"fk_work_orders_assets_asset_id\"",
                "ERROR",
                "ERROR",
                PostgresErrorCodes.ForeignKeyViolation));
        Assert.False(WorkOrderGenerationService.IsPmocPeriodUniqueViolation(fk));

        Assert.False(
            WorkOrderGenerationService.IsPmocPeriodUniqueViolation(
                new DbUpdateException("conflict", new InvalidOperationException("nope"))));
    }

    [Fact]
    public async Task Unique_violation_recovery_clears_tracker_so_next_asset_can_generate()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await CreatePlanAsync(harness, "Tracker");
        var assetA = await CreateMatchingAssetAsync(harness, "A");
        var assetB = await CreateMatchingAssetAsync(harness, "B");
        var generator = CreateGenerator(harness);
        var scheduled = new DateOnly(2026, 9, 18);

        var zombie = new WorkOrder
        {
            TenantId = plan.TenantId,
            AssetId = assetA.Id,
            MaintenancePlanId = plan.Id,
            Status = WorkOrderStatus.Pending,
            ScheduledDate = scheduled,
            SourcePlanName = "zombie",
        };
        harness.Db.WorkOrders.Add(zombie);
        Assert.Contains(
            harness.Db.ChangeTracker.Entries<WorkOrder>(),
            entry => entry.State == EntityState.Added && entry.Entity.Id == zombie.Id);

        var unique = new DbUpdateException(
            "conflict",
            new PostgresException(
                "duplicate key value violates unique constraint \"ux_os_work_orders_pmoc_period\"",
                "ERROR",
                "ERROR",
                PostgresErrorCodes.UniqueViolation));
        Assert.True(WorkOrderGenerationService.IsPmocPeriodUniqueViolation(unique));
        generator.DiscardFailedGeneration();

        Assert.DoesNotContain(
            harness.Db.ChangeTracker.Entries<WorkOrder>(),
            entry => entry.State == EntityState.Added && entry.Entity.Id == zombie.Id);

        var created = await generator.GenerateAsync(
            new GenerateWorkOrderCommand(plan.Id, assetB.Id, scheduled, null),
            CancellationToken.None);

        Assert.Equal(assetB.Id, created.AssetId);
        Assert.False(await harness.Db.WorkOrders.AnyAsync(item => item.Id == zombie.Id));
        Assert.Equal(1, await harness.Db.WorkOrders.CountAsync());
    }

    [Fact(Skip = "InMemory EF does not enforce ux_os_work_orders_pmoc_period; do not fake a concurrent unique test.")]
    public void Concurrent_duplicate_requires_postgres_unique_index()
    {
    }

    [Fact]
    public async Task Canceled_work_order_allows_regeneration_same_key()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await CreatePlanAsync(harness, "Canceled-regen");
        var asset = await CreateMatchingAssetAsync(harness);
        var scheduled = new DateOnly(2026, 9, 18);
        var generator = CreateGenerator(harness);

        var first = await generator.GenerateAsync(
            new GenerateWorkOrderCommand(plan.Id, asset.Id, scheduled, null),
            CancellationToken.None);
        var stored = await harness.Db.WorkOrders.SingleAsync(item => item.Id == first.Id);
        stored.Status = WorkOrderStatus.Canceled;
        await harness.Db.SaveChangesAsync();

        var second = await generator.GenerateAsync(
            new GenerateWorkOrderCommand(plan.Id, asset.Id, scheduled, null),
            CancellationToken.None);

        Assert.Equal(WorkOrderStatus.Pending, second.Status);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, await harness.Db.WorkOrders.CountAsync());
        Assert.Equal(1, await harness.Db.WorkOrders.CountAsync(item => item.Status == WorkOrderStatus.Pending));
    }

    [Fact]
    public async Task List_filters_by_maintenance_plan_and_asset_and_tenant()
    {
        await using var harness = await WorkOrderRbacHarness.CreateAsync();
        harness.SetUser(harness.Creator);
        var planA = await AddPlanAsync(harness, "Plan A");
        var planB = await AddPlanAsync(harness, "Plan B");
        var extraAsset = await AddAssetAsync(harness, "Q2");
        var woPlanAAsset1 = harness.AddWorkOrder(harness.Creator.Id);
        woPlanAAsset1.MaintenancePlanId = planA.Id;
        woPlanAAsset1.SourcePlanName = planA.Name;
        var woPlanBAsset1 = harness.AddWorkOrder(harness.Creator.Id);
        woPlanBAsset1.MaintenancePlanId = planB.Id;
        var woPlanAAsset2 = new WorkOrder
        {
            TenantId = harness.TenantProvider.TenantId!.Value,
            AssetId = extraAsset.Id,
            MaintenancePlanId = planA.Id,
            AssignedUserId = harness.Creator.Id,
            Status = WorkOrderStatus.Pending,
            ScheduledDate = DateOnly.FromDateTime(DateTime.UtcNow),
        };
        harness.Db.WorkOrders.Add(woPlanAAsset2);
        var foreign = new WorkOrder
        {
            TenantId = Guid.NewGuid(),
            AssetId = extraAsset.Id,
            MaintenancePlanId = planA.Id,
            Status = WorkOrderStatus.Pending,
            ScheduledDate = DateOnly.FromDateTime(DateTime.UtcNow),
        };
        harness.Db.WorkOrders.Add(foreign);
        await harness.Db.SaveChangesAsync();

        var byPlan = await harness.Service.ListAsync(assetId: null, planA.Id, CancellationToken.None);
        Assert.Equal(2, byPlan.Count);
        Assert.All(byPlan, item => Assert.Equal(planA.Id, item.MaintenancePlanId));
        Assert.DoesNotContain(byPlan, item => item.Id == foreign.Id);

        var combined = await harness.Service.ListAsync(harness.AssetId, planA.Id, CancellationToken.None);
        Assert.Single(combined);
        Assert.Equal(woPlanAAsset1.Id, combined[0].Id);

        var byAsset = await harness.Service.ListAsync(harness.AssetId, maintenancePlanId: null, CancellationToken.None);
        Assert.Equal(2, byAsset.Count);
        Assert.All(byAsset, item => Assert.Equal(harness.AssetId, item.AssetId));
    }

    [Fact]
    public async Task Cross_tenant_cannot_from_plan_or_list_foreign_work_orders()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await CreatePlanAsync(harness, "Tenant A plan");
        var asset = await CreateMatchingAssetAsync(harness);
        var generatorA = CreateGenerator(harness);
        var created = await generatorA.GenerateAsync(
            new GenerateWorkOrderCommand(plan.Id, asset.Id, new DateOnly(2026, 9, 18), null),
            CancellationToken.None);
        var originalTenant = harness.TenantProvider.TenantId;
        var tenantB = new Tenant("OS Other", "88888888000191", subdomain: "os-gen-b");
        harness.Db.Tenants.Add(tenantB);
        await harness.Db.SaveChangesAsync();
        harness.TenantProvider.TenantId = tenantB.Id;

        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateGenerator(harness).GenerateAsync(
                new GenerateWorkOrderCommand(plan.Id, asset.Id, new DateOnly(2026, 9, 19), null),
                CancellationToken.None));
        Assert.Contains(plan.Id.ToString(), ex.Message, StringComparison.Ordinal);

        harness.TenantProvider.TenantId = originalTenant;
        Assert.True(await harness.Db.WorkOrders.AnyAsync(item => item.Id == created.Id));
        harness.TenantProvider.TenantId = tenantB.Id;
        Assert.Empty(await harness.Db.WorkOrders.ToListAsync());
    }

    [Fact]
    public async Task Inactive_plan_and_empty_tasks_are_http_400()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var inactive = await CreatePlanAsync(harness, "Inactive", isActive: false);
        var asset = await CreateMatchingAssetAsync(harness);
        var generator = CreateGenerator(harness);

        var inactiveEx = await Assert.ThrowsAsync<ArgumentException>(() =>
            generator.GenerateAsync(
                new GenerateWorkOrderCommand(inactive.Id, asset.Id, new DateOnly(2026, 9, 18), null),
                CancellationToken.None));
        Assert.Contains("not active", inactiveEx.Message, StringComparison.OrdinalIgnoreCase);

        var emptyPlan = new MaintenancePlan
        {
            TenantId = harness.TenantProvider.TenantId!.Value,
            UnitId = harness.UnitId,
            Name = "Empty",
            Frequency = MaintenanceFrequency.Daily,
            AssetCategoryId = harness.CategoryId,
            IsActive = true,
        };
        harness.Db.MaintenancePlans.Add(emptyPlan);
        await harness.Db.SaveChangesAsync();

        var emptyEx = await Assert.ThrowsAsync<ArgumentException>(() =>
            generator.GenerateAsync(
                new GenerateWorkOrderCommand(emptyPlan.Id, asset.Id, new DateOnly(2026, 9, 18), null),
                CancellationToken.None));
        Assert.Contains("task", emptyEx.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(await harness.Db.WorkOrders.AnyAsync());
    }

    [Fact]
    public async Task Ineligible_assets_are_rejected()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await CreatePlanAsync(harness, "Eligibility");
        var generator = CreateGenerator(harness);
        var scheduled = new DateOnly(2026, 9, 18);

        var inactiveAsset = await CreateMatchingAssetAsync(harness, "INACT", AssetStatus.Inactive);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            generator.GenerateAsync(
                new GenerateWorkOrderCommand(plan.Id, inactiveAsset.Id, scheduled, null),
                CancellationToken.None));

        var deleting = await CreateMatchingAssetAsync(harness, "DEL");
        deleting.ScheduledDeletionAt = DateTimeOffset.UtcNow;
        await harness.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            generator.GenerateAsync(
                new GenerateWorkOrderCommand(plan.Id, deleting.Id, scheduled, null),
                CancellationToken.None));

        var otherCategory = new AssetCategory
        {
            TenantId = harness.TenantProvider.TenantId!.Value,
            Name = "Other",
        };
        harness.Db.AssetCategories.Add(otherCategory);
        await harness.Db.SaveChangesAsync();
        var mismatched = await CreateMatchingAssetAsync(harness, "MIS");
        mismatched.CategoryId = otherCategory.Id;
        await harness.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            generator.GenerateAsync(
                new GenerateWorkOrderCommand(plan.Id, mismatched.Id, scheduled, null),
                CancellationToken.None));

        var missing = Guid.NewGuid();
        var notFound = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            generator.GenerateAsync(
                new GenerateWorkOrderCommand(plan.Id, missing, scheduled, null),
                CancellationToken.None));
        Assert.Contains(missing.ToString(), notFound.Message, StringComparison.Ordinal);
        Assert.False(await harness.Db.WorkOrders.AnyAsync());
    }

    [Fact]
    public async Task Assignee_without_execute_is_rejected_like_create()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        await SeedWorkOrdersModuleAsync(harness);
        var reader = await AddUserWithKeysAsync(harness, "reader", Permissions.Os.WorkOrdersRead);
        var executor = await AddUserWithKeysAsync(
            harness,
            "executor",
            Permissions.Os.WorkOrdersExecute);
        var plan = await CreatePlanAsync(harness, "Assignee");
        var asset = await CreateMatchingAssetAsync(harness);
        var generator = CreateGenerator(harness);
        var scheduled = new DateOnly(2026, 9, 18);

        var omitted = await generator.GenerateAsync(
            new GenerateWorkOrderCommand(plan.Id, asset.Id, scheduled, null),
            CancellationToken.None);
        Assert.Null(omitted.AssignedUserId);

        var denied = await Assert.ThrowsAsync<ArgumentException>(() =>
            generator.GenerateAsync(
                new GenerateWorkOrderCommand(plan.Id, asset.Id, scheduled.AddDays(1), reader.Id),
                CancellationToken.None));
        Assert.Contains("cannot execute work orders", denied.Message, StringComparison.Ordinal);

        var assigned = await generator.GenerateAsync(
            new GenerateWorkOrderCommand(plan.Id, asset.Id, scheduled.AddDays(2), executor.Id),
            CancellationToken.None);
        Assert.Equal(executor.Id, assigned.AssignedUserId);
    }

    [Fact]
    public async Task Manual_create_does_not_snapshot_plan_tasks_or_source_name()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await CreatePlanAsync(harness, "Should not copy");
        var asset = await CreateMatchingAssetAsync(harness);
        var service = CreateWorkOrderService(harness);

        var created = await service.CreateAsync(
            new CreateWorkOrderRequest
            {
                AssetId = asset.Id,
                ScheduledDate = new DateOnly(2026, 9, 18),
                Tasks =
                [
                    new CreateWorkOrderTaskDto
                    {
                        Title = "Client task",
                        InputType = TaskInputType.Text,
                        Order = 1,
                    },
                ],
            },
            CancellationToken.None);

        Assert.Null(created.MaintenancePlanId);
        Assert.Null(created.SourcePlanName);
        Assert.Equal("Client task", created.Tasks.Single().Title);
        Assert.Null(created.Tasks.Single().PlanTaskId);
        Assert.Equal("Should not copy", plan.Name);
    }

    [Fact]
    public void From_plan_requires_create_permission_and_os_module()
    {
        var method = typeof(WorkOrdersController).GetMethod(nameof(WorkOrdersController.CreateFromPlan));
        Assert.NotNull(method);
        var permission = method!.GetCustomAttribute<RequirePermissionAttribute>();
        Assert.NotNull(permission);
        Assert.Equal(PermissionPolicies.Name(Permissions.Os.WorkOrdersCreate), permission!.Policy);
        var httpPost = method.GetCustomAttribute<HttpPostAttribute>();
        Assert.NotNull(httpPost);
        Assert.Equal("from-plan", httpPost!.Template);
        Assert.Equal(
            PlatformModules.WorkOrders,
            typeof(WorkOrdersController).GetCustomAttribute<RequireActiveModuleAttribute>()!.ModuleKey);

        var list = typeof(WorkOrdersController).GetMethod(nameof(WorkOrdersController.List));
        Assert.Equal(
            PermissionPolicies.Name(Permissions.Os.WorkOrdersRead),
            list!.GetCustomAttribute<RequirePermissionAttribute>()!.Policy);
    }

    [Fact]
    public async Task Controller_maps_inactive_plan_to_400()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await CreatePlanAsync(harness, "Http-inactive", isActive: false);
        var asset = await CreateMatchingAssetAsync(harness);
        var controller = CreateController(harness, CreateGenerator(harness));

        var result = await controller.CreateFromPlan(
            new GenerateWorkOrderFromPlanRequest
            {
                PlanId = plan.Id,
                AssetId = asset.Id,
                ScheduledDate = new DateOnly(2026, 9, 18),
            },
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(badRequest.Value));
        Assert.Contains(
            "not active",
            doc.RootElement.GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static WorkOrdersController CreateController(
        BulkCreateAssetsHarness harness,
        IWorkOrderGenerationService generator) =>
        new(CreateWorkOrderService(harness), generator, harness.CreateRegistry());

    private static WorkOrderGenerationService CreateGenerator(BulkCreateAssetsHarness harness) =>
        new(
            harness.Db,
            harness.TenantProvider,
            TestPermissionResolvers.Create(harness.Db, harness.TenantProvider));

    private static MaintenancePlanService CreatePlanService(BulkCreateAssetsHarness harness) =>
        new(harness.Db, harness.TenantProvider, harness.CreateRegistry());

    private static WorkOrderService CreateWorkOrderService(BulkCreateAssetsHarness harness)
    {
        var http = new FakeHttpContextAccessor();
        var resolver = TestPermissionResolvers.Create(harness.Db, harness.TenantProvider);
        var grantGuard = new RbacGrantGuard(harness.Db, resolver, NullLogger<RbacGrantGuard>.Instance);
        var users = new UserDirectoryService(
            harness.Db,
            harness.TenantProvider,
            new FakePlatformAdminChecker(),
            resolver,
            grantGuard,
            new FakeTrialGuard(),
            new NotificationQueue(),
            new ConfigurationBuilder().Build(),
            new FakeHostEnvironment(),
            NullLogger<UserDirectoryService>.Instance);

        return new WorkOrderService(
            harness.Db,
            harness.TenantProvider,
            http,
            users,
            resolver,
            harness.CreateRegistry());
    }

    private static async Task<MaintenancePlanResponse> CreatePlanAsync(
        BulkCreateAssetsHarness harness,
        string name,
        bool autoGenerateEnabled = false,
        bool isActive = true,
        params CreatePlanTaskDto[] tasks)
    {
        var request = new CreateMaintenancePlanRequest
        {
            UnitId = harness.UnitId,
            Name = name,
            Frequency = MaintenanceFrequency.Daily,
            AssetCategoryId = harness.CategoryId,
            IsActive = isActive,
            AutoGenerateEnabled = autoGenerateEnabled,
            Tasks = tasks.Length == 0
                ?
                [
                    new CreatePlanTaskDto
                    {
                        Title = "Filtro",
                        InputType = TaskInputType.Checkbox,
                        Order = 1,
                    },
                ]
                : [.. tasks],
        };

        return await CreatePlanService(harness).CreatePlanWithTasksAsync(request, CancellationToken.None);
    }

    private static async Task<Asset> CreateMatchingAssetAsync(
        BulkCreateAssetsHarness harness,
        string? tag = null,
        AssetStatus status = AssetStatus.Active)
    {
        var asset = new Asset
        {
            TenantId = harness.TenantProvider.TenantId!.Value,
            UnitId = harness.UnitId,
            CategoryId = harness.CategoryId,
            FamilyId = harness.FamilyId,
            Name = "Split",
            Tag = tag ?? $"AC-{Guid.NewGuid():N}"[..12],
            Status = status,
        };
        harness.Db.Assets.Add(asset);
        await harness.Db.SaveChangesAsync();
        return asset;
    }

    private static UpdateMaintenancePlanRequest HeaderUpdate(
        BulkCreateAssetsHarness harness,
        string name,
        bool isActive,
        bool autoGenerateEnabled) =>
        new()
        {
            UnitId = harness.UnitId,
            Name = name,
            Frequency = MaintenanceFrequency.Daily,
            AssetCategoryId = harness.CategoryId,
            IsActive = isActive,
            AutoGenerateEnabled = autoGenerateEnabled,
        };

    private static async Task SeedWorkOrdersModuleAsync(BulkCreateAssetsHarness harness)
    {
        var tenantId = harness.TenantProvider.TenantId!.Value;
        foreach (var entry in PermissionCatalog.All)
        {
            harness.Db.Permissions.Add(new Permission(entry.Key, entry.Name, entry.Description, entry.ModuleKey));
        }

        harness.Db.TenantModules.Add(new TenantModule(tenantId, PlatformModules.WorkOrders, isActive: true));
        await harness.Db.SaveChangesAsync();
    }

    private static async Task<User> AddUserWithKeysAsync(
        BulkCreateAssetsHarness harness,
        string name,
        params string[] keys)
    {
        var tenantId = harness.TenantProvider.TenantId!.Value;
        var role = new Role(tenantId, name);
        harness.Db.Roles.Add(role);
        foreach (var key in keys)
        {
            var permission = harness.Db.Permissions.Local.First(item => item.Key == key);
            harness.Db.RolePermissions.Add(new RolePermission(role.Id, permission.Id));
        }

        var user = new User(tenantId, $"{name}-{Guid.NewGuid():N}"[..20], name, $"{name}@test.com");
        harness.Db.Users.Add(user);
        harness.Db.UserRoles.Add(new UserRole(user.Id, role.Id));
        await harness.Db.SaveChangesAsync();
        return user;
    }

    private static async Task<MaintenancePlan> AddPlanAsync(WorkOrderRbacHarness harness, string name)
    {
        var asset = await harness.Db.Assets.SingleAsync(item => item.Id == harness.AssetId);
        var plan = new MaintenancePlan
        {
            TenantId = harness.TenantProvider.TenantId!.Value,
            UnitId = asset.UnitId,
            Name = name,
            Frequency = MaintenanceFrequency.Daily,
            AssetCategoryId = asset.CategoryId,
            IsActive = true,
        };
        plan.AddTask(new PlanTask
        {
            TenantId = plan.TenantId,
            MaintenancePlanId = plan.Id,
            Title = "Filtro",
            InputType = TaskInputType.Checkbox,
            IsMandatory = true,
            Order = 1,
        });
        harness.Db.MaintenancePlans.Add(plan);
        await harness.Db.SaveChangesAsync();
        return plan;
    }

    private static async Task<Asset> AddAssetAsync(WorkOrderRbacHarness harness, string tag)
    {
        var existing = await harness.Db.Assets.SingleAsync(item => item.Id == harness.AssetId);
        var asset = new Asset
        {
            TenantId = existing.TenantId,
            UnitId = existing.UnitId,
            CategoryId = existing.CategoryId,
            FamilyId = existing.FamilyId,
            Name = tag,
            Tag = tag,
            Status = AssetStatus.Active,
        };
        harness.Db.Assets.Add(asset);
        await harness.Db.SaveChangesAsync();
        return asset;
    }
}
