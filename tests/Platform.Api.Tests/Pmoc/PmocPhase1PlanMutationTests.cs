using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Api.Authorization;
using Platform.Api.Modules.Pmoc;
using Platform.Api.Modules.Pmoc.Controllers;
using Platform.Api.Modules.Pmoc.Dtos;
using Platform.Api.Modules.Pmoc.Services;
using Platform.Api.Tests.Assets;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence.Seed;

namespace Platform.Api.Tests.Pmoc;

public sealed class PmocPhase1PlanMutationTests
{
    [Fact]
    public async Task Unused_plan_delete_removes_plan_and_tasks()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var service = CreateService(harness);
        var created = await CreateCustomPlanAsync(harness, service, "Unused");

        Assert.True(await service.DeleteAsync(created.Id, CancellationToken.None));

        Assert.Null(await service.GetByIdAsync(created.Id, CancellationToken.None));
        Assert.False(
            await harness.Db.MaintenancePlans.IgnoreQueryFilters()
                .AnyAsync(plan => plan.Id == created.Id));
        Assert.False(
            await harness.Db.PlanTasks.IgnoreQueryFilters()
                .AnyAsync(task => task.MaintenancePlanId == created.Id));
    }

    [Fact]
    public async Task Used_plan_delete_throws_plan_in_use_and_keeps_links()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var service = CreateService(harness);
        var created = await CreateCustomPlanAsync(harness, service, "Used");
        var workOrder = await AddLinkedWorkOrderAsync(
            harness,
            created,
            WorkOrderStatus.Pending,
            snapshotValue: "checked");

        var ex = await Assert.ThrowsAsync<PlanInUseException>(
            () => service.DeleteAsync(created.Id, CancellationToken.None));

        Assert.Equal("PLAN_IN_USE", PlanInUseException.Code);
        Assert.Contains("IsActive=false", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(await service.GetByIdAsync(created.Id, CancellationToken.None));
        var stored = await harness.Db.WorkOrders.SingleAsync(item => item.Id == workOrder.Id);
        Assert.Equal(created.Id, stored.MaintenancePlanId);
    }

    [Fact]
    public async Task Canceled_work_order_still_counts_as_used()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var service = CreateService(harness);
        var created = await CreateCustomPlanAsync(harness, service, "Canceled-used");
        var workOrder = await AddLinkedWorkOrderAsync(
            harness,
            created,
            WorkOrderStatus.Canceled,
            snapshotValue: null);

        var ex = await Assert.ThrowsAsync<PlanInUseException>(
            () => service.DeleteAsync(created.Id, CancellationToken.None));

        Assert.Equal("PLAN_IN_USE", PlanInUseException.Code);
        Assert.Contains("IsActive=false", ex.Message, StringComparison.Ordinal);
        Assert.True(await harness.Db.MaintenancePlans.AnyAsync(plan => plan.Id == created.Id));
        Assert.Equal(
            created.Id,
            (await harness.Db.WorkOrders.SingleAsync(item => item.Id == workOrder.Id)).MaintenancePlanId);
    }

    [Fact]
    public async Task Controller_maps_used_delete_to_409_plan_in_use()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var service = CreateService(harness);
        var created = await CreateCustomPlanAsync(harness, service, "Http-used");
        await AddLinkedWorkOrderAsync(harness, created, WorkOrderStatus.Pending, snapshotValue: "x");
        var controller = new MaintenancePlansController(
            service,
            new MaintenancePlanCoverageService(harness.Db, harness.TenantProvider, TimeProvider.System),
            harness.CreateRegistry());

        var result = await controller.Delete(created.Id, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(conflict.Value));
        Assert.Equal("PLAN_IN_USE", doc.RootElement.GetProperty("code").GetString());
        Assert.Contains(
            "IsActive=false",
            doc.RootElement.GetProperty("error").GetString(),
            StringComparison.Ordinal);
        Assert.True(await harness.Db.MaintenancePlans.AnyAsync(plan => plan.Id == created.Id));
    }

    [Fact]
    public async Task Controller_unused_delete_returns_204()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var service = CreateService(harness);
        var created = await CreateCustomPlanAsync(harness, service, "Http-unused");
        var controller = new MaintenancePlansController(
            service,
            new MaintenancePlanCoverageService(harness.Db, harness.TenantProvider, TimeProvider.System),
            harness.CreateRegistry());

        var result = await controller.Delete(created.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.False(await harness.Db.MaintenancePlans.AnyAsync(plan => plan.Id == created.Id));
    }

    [Fact]
    public async Task Replace_tasks_add_update_remove_and_reorder()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var service = CreateService(harness);
        var created = await service.CreatePlanWithTasksAsync(
            Request(
                harness,
                "Checklist",
                PlanTaskDto("Keep", 1, """{"min":1}"""),
                PlanTaskDto("Drop", 2),
                PlanTaskDto("Move", 3)),
            CancellationToken.None);
        var keepId = created.Tasks.Single(task => task.Title == "Keep").Id;
        var moveId = created.Tasks.Single(task => task.Title == "Move").Id;

        var updated = await service.ReplaceTasksAsync(
            created.Id,
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
                    new ReplacePlanTaskDto
                    {
                        Id = keepId,
                        Title = "Keep updated",
                        InputType = TaskInputType.Number,
                        IsMandatory = false,
                        Order = 3,
                        Configuration = """{"min":9}""",
                    },
                    new ReplacePlanTaskDto
                    {
                        Id = moveId,
                        Title = "Move",
                        InputType = TaskInputType.Checkbox,
                        Order = 2,
                    },
                ],
            },
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(
            ["Brand new", "Move", "Keep updated"],
            updated.Tasks.Select(task => task.Title).ToArray());
        Assert.Equal([1, 2, 3], updated.Tasks.Select(task => task.Order).ToArray());
        var kept = updated.Tasks.Single(task => task.Id == keepId);
        Assert.Equal(TaskInputType.Number, kept.InputType);
        Assert.False(kept.IsMandatory);
        Assert.Equal("""{"min":9}""", kept.Configuration);
        Assert.DoesNotContain(updated.Tasks, task => task.Title == "Drop");
        Assert.Equal(3, await harness.Db.PlanTasks.CountAsync(task => task.MaintenancePlanId == created.Id));
    }

    [Fact]
    public async Task Replace_tasks_requires_at_least_one_task()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var service = CreateService(harness);
        var created = await CreateCustomPlanAsync(harness, service, "Empty-replace");

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.ReplaceTasksAsync(
                created.Id,
                new ReplacePlanTasksRequest { Tasks = [] },
                CancellationToken.None));

        Assert.Contains("At least one", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single((await service.GetByIdAsync(created.Id, CancellationToken.None))!.Tasks);
    }

    [Fact]
    public async Task Replace_tasks_invalid_json_configuration_is_argument_exception()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var service = CreateService(harness);
        var created = await CreateCustomPlanAsync(harness, service, "Bad-json");
        var taskId = created.Tasks[0].Id;

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.ReplaceTasksAsync(
                created.Id,
                new ReplacePlanTasksRequest
                {
                    Tasks =
                    [
                        new ReplacePlanTaskDto
                        {
                            Id = taskId,
                            Title = "Broken",
                            InputType = TaskInputType.Checkbox,
                            Order = 1,
                            Configuration = "{not-json",
                        },
                    ],
                },
                CancellationToken.None));

        Assert.Contains("invalid Configuration JSON", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Controller_maps_invalid_replace_to_400()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var service = CreateService(harness);
        var created = await CreateCustomPlanAsync(harness, service, "Http-400");
        var controller = new MaintenancePlansController(
            service,
            new MaintenancePlanCoverageService(harness.Db, harness.TenantProvider, TimeProvider.System),
            harness.CreateRegistry());

        var result = await controller.ReplaceTasks(
            created.Id,
            new ReplacePlanTasksRequest { Tasks = [] },
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(badRequest.Value));
        Assert.Contains(
            "At least one",
            doc.RootElement.GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Historical_work_order_tasks_are_immutable_except_setnull_plan_task_id()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var service = CreateService(harness);
        var created = await service.CreatePlanWithTasksAsync(
            Request(
                harness,
                "Snapshot",
                PlanTaskDto("Filter", 1, """{"min":1}"""),
                PlanTaskDto("Photo", 2, """{"required":true}""")),
            CancellationToken.None);
        var filter = created.Tasks.Single(task => task.Title == "Filter");
        var photo = created.Tasks.Single(task => task.Title == "Photo");
        var workOrder = await AddLinkedWorkOrderAsync(
            harness,
            created,
            WorkOrderStatus.InProgress,
            snapshotValue: "done",
            includeAllPlanTasks: true);
        var originalTasks = await harness.Db.WorkOrderTasks
            .AsNoTracking()
            .Where(task => task.WorkOrderId == workOrder.Id)
            .OrderBy(task => task.Order)
            .ToListAsync();
        Assert.Equal(2, originalTasks.Count);

        await service.ReplaceTasksAsync(
            created.Id,
            new ReplacePlanTasksRequest
            {
                Tasks =
                [
                    new ReplacePlanTaskDto
                    {
                        Id = filter.Id,
                        Title = "Filter rewritten",
                        InputType = TaskInputType.Text,
                        IsMandatory = false,
                        Order = 2,
                        Configuration = """{"min":99}""",
                    },
                    new ReplacePlanTaskDto
                    {
                        Title = "Only future",
                        InputType = TaskInputType.Checkbox,
                        Order = 1,
                    },
                ],
            },
            CancellationToken.None);

        harness.Db.ChangeTracker.Clear();
        var after = await harness.Db.WorkOrderTasks
            .AsNoTracking()
            .Where(task => task.WorkOrderId == workOrder.Id)
            .OrderBy(task => task.Order)
            .ToListAsync();

        Assert.Equal(2, after.Count);
        Assert.Equal(originalTasks.Select(ImmutableSnapshot).ToArray(), after.Select(ImmutableSnapshot).ToArray());
        Assert.Equal(filter.Id, after.Single(task => task.Title == "Filter").PlanTaskId);
        Assert.Null(after.Single(task => task.Title == "Photo").PlanTaskId);
        Assert.Equal("done", after.Single(task => task.Title == "Filter").Value);
        Assert.DoesNotContain(after, task => task.Title == "Filter rewritten");
        Assert.DoesNotContain(after, task => task.Title == "Only future");
    }

    [Fact]
    public void Put_tasks_requires_plans_write_permission()
    {
        var method = typeof(MaintenancePlansController).GetMethod(
            nameof(MaintenancePlansController.ReplaceTasks));
        Assert.NotNull(method);
        var permission = method!.GetCustomAttribute<RequirePermissionAttribute>();
        Assert.NotNull(permission);
        Assert.Equal(PermissionPolicies.Name(Permissions.Pmoc.PlansWrite), permission!.Policy);
        var httpPut = method.GetCustomAttribute<HttpPutAttribute>();
        Assert.NotNull(httpPut);
        Assert.Equal("{id:guid}/tasks", httpPut!.Template);
        Assert.Equal(
            PlatformModules.Pmoc,
            typeof(MaintenancePlansController).GetCustomAttribute<RequireActiveModuleAttribute>()!.ModuleKey);
    }

    [Fact]
    public void Update_request_does_not_bind_origin_fields()
    {
        var type = typeof(UpdateMaintenancePlanRequest);
        Assert.Null(type.GetProperty(nameof(MaintenancePlan.OriginKind)));
        Assert.Null(type.GetProperty(nameof(MaintenancePlan.SourceTemplateId)));
        Assert.Null(type.GetProperty(nameof(MaintenancePlan.SourceTemplateVersion)));
        Assert.NotNull(type.GetProperty(nameof(UpdateMaintenancePlanRequest.AutoGenerateEnabled)));
    }

    [Fact]
    public async Task Cross_tenant_cannot_mutate_foreign_plan()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var service = CreateService(harness);
        var created = await CreateCustomPlanAsync(harness, service, "Tenant-A");
        var originalName = created.Name;
        var originalTenant = harness.TenantProvider.TenantId;
        var tenantB = new Tenant("Tenant B", "88888888000191", subdomain: "pmoc-tenant-b");
        harness.Db.Tenants.Add(tenantB);
        await harness.Db.SaveChangesAsync();
        harness.TenantProvider.TenantId = tenantB.Id;

        Assert.Null(await service.GetByIdAsync(created.Id, CancellationToken.None));
        Assert.Null(
            await service.UpdateAsync(
                created.Id,
                HeaderUpdate(harness, "Hacked", isActive: false, autoGenerateEnabled: true),
                CancellationToken.None));
        Assert.Null(
            await service.ReplaceTasksAsync(
                created.Id,
                new ReplacePlanTasksRequest
                {
                    Tasks = [new ReplacePlanTaskDto { Title = "Hacked", InputType = TaskInputType.Checkbox, Order = 1 }],
                },
                CancellationToken.None));
        Assert.False(await service.DeleteAsync(created.Id, CancellationToken.None));

        harness.TenantProvider.TenantId = originalTenant;
        var stillThere = await service.GetByIdAsync(created.Id, CancellationToken.None);
        Assert.NotNull(stillThere);
        Assert.Equal(originalName, stillThere!.Name);
        Assert.Equal("Filtro", Assert.Single(stillThere.Tasks).Title);
    }

    [Fact]
    public async Task Responses_include_lineage_and_auto_fields()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var service = CreateService(harness);
        var created = await CreateCustomPlanAsync(harness, service, "Lineage");

        AssertLineage(created, MaintenancePlanOriginKind.Custom, null, null, autoGenerateEnabled: false);

        var listed = Assert.Single(
            await service.ListAsync(CancellationToken.None),
            plan => plan.Id == created.Id);
        AssertLineage(listed, MaintenancePlanOriginKind.Custom, null, null, autoGenerateEnabled: false);

        var byId = await service.GetByIdAsync(created.Id, CancellationToken.None);
        Assert.NotNull(byId);
        AssertLineage(byId!, MaintenancePlanOriginKind.Custom, null, null, autoGenerateEnabled: false);

        var updated = await service.UpdateAsync(
            created.Id,
            HeaderUpdate(harness, "Lineage", isActive: true, autoGenerateEnabled: true),
            CancellationToken.None);
        Assert.NotNull(updated);
        AssertLineage(updated!, MaintenancePlanOriginKind.Custom, null, null, autoGenerateEnabled: true);
    }

    [Fact]
    public async Task Header_put_sets_auto_generate_independently_and_preserves_origin()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var service = CreateService(harness);
        var sourceId = GlobalTemplateSeed.AnvisaNr10TemplateId;
        var plan = new MaintenancePlan
        {
            TenantId = harness.TenantProvider.TenantId!.Value,
            UnitId = harness.UnitId,
            Name = "Cloned",
            Description = "From library",
            Frequency = MaintenanceFrequency.Monthly,
            AssetCategoryId = harness.CategoryId,
            IsActive = true,
            OriginKind = MaintenancePlanOriginKind.RolvixTemplate,
            SourceTemplateId = sourceId,
            SourceTemplateVersion = 1,
            AutoGenerateEnabled = false,
        };
        plan.AddTask(
            new PlanTask
            {
                TenantId = plan.TenantId,
                MaintenancePlanId = plan.Id,
                Title = "Seeded",
                InputType = TaskInputType.Checkbox,
                IsMandatory = true,
                Order = 1,
            });
        harness.Db.MaintenancePlans.Add(plan);
        await harness.Db.SaveChangesAsync();

        var updated = await service.UpdateAsync(
            plan.Id,
            new UpdateMaintenancePlanRequest
            {
                UnitId = harness.UnitId,
                Name = "Cloned renamed",
                Description = "Still cloned",
                Frequency = MaintenanceFrequency.Weekly,
                AssetCategoryId = harness.CategoryId,
                IsActive = false,
                AutoGenerateEnabled = true,
            },
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("Cloned renamed", updated!.Name);
        Assert.False(updated.IsActive);
        Assert.True(updated.AutoGenerateEnabled);
        Assert.Equal(MaintenanceFrequency.Weekly, updated.Frequency);
        AssertLineage(updated, MaintenancePlanOriginKind.RolvixTemplate, sourceId, 1, autoGenerateEnabled: true);
        var stored = await harness.Db.MaintenancePlans
            .Include(item => item.Tasks)
            .SingleAsync(item => item.Id == plan.Id);
        Assert.Equal(MaintenancePlanOriginKind.RolvixTemplate, stored.OriginKind);
        Assert.Equal(sourceId, stored.SourceTemplateId);
        Assert.Equal(1, stored.SourceTemplateVersion);
        Assert.Equal("Seeded", Assert.Single(stored.Tasks).Title);
    }

    [Fact]
    public async Task Create_omits_auto_generate_false_and_honors_true()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var service = CreateService(harness);

        var omitted = await service.CreatePlanWithTasksAsync(
            Request(harness, "Omit-auto"),
            CancellationToken.None);
        Assert.False(omitted.AutoGenerateEnabled);
        Assert.Equal(MaintenancePlanOriginKind.Custom, omitted.OriginKind);
        Assert.Null(omitted.SourceTemplateId);
        Assert.Null(omitted.SourceTemplateVersion);

        var enabled = await service.CreatePlanWithTasksAsync(
            Request(harness, "Enable-auto", autoGenerateEnabled: true, tasks: []),
            CancellationToken.None);
        Assert.True(enabled.AutoGenerateEnabled);
        Assert.Equal(MaintenancePlanOriginKind.Custom, enabled.OriginKind);
        Assert.Null(enabled.SourceTemplateId);
        Assert.Null(enabled.SourceTemplateVersion);
    }

    [Fact]
    public async Task Cross_plan_task_id_is_rejected()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var service = CreateService(harness);
        var planA = await CreateCustomPlanAsync(harness, service, "Plan-A");
        var planB = await CreateCustomPlanAsync(harness, service, "Plan-B");
        var foreignTaskId = planB.Tasks[0].Id;

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.ReplaceTasksAsync(
                planA.Id,
                new ReplacePlanTasksRequest
                {
                    Tasks =
                    [
                        new ReplacePlanTaskDto
                        {
                            Id = foreignTaskId,
                            Title = "Stolen",
                            InputType = TaskInputType.Checkbox,
                            Order = 1,
                        },
                    ],
                },
                CancellationToken.None));

        Assert.Equal("Filtro", (await service.GetByIdAsync(planA.Id, CancellationToken.None))!.Tasks[0].Title);
        Assert.Equal("Filtro", (await service.GetByIdAsync(planB.Id, CancellationToken.None))!.Tasks[0].Title);
    }

    [Fact]
    public void Restrict_fk_exception_maps_to_plan_in_use_not_unrelated()
    {
        var restrict = new DbUpdateException(
            "conflict",
            new PostgresException(
                "update or delete on table \"maintenance_plans\" violates foreign key constraint \"fk_work_orders_maintenance_plans_maintenance_plan_id\" on table \"work_orders\"",
                "ERROR",
                "ERROR",
                PostgresErrorCodes.ForeignKeyViolation));
        Assert.True(MaintenancePlanService.IsWorkOrderPlanRestrictViolation(restrict));

        var unique = new DbUpdateException(
            "conflict",
            new PostgresException(
                "duplicate key",
                "ERROR",
                "ERROR",
                PostgresErrorCodes.UniqueViolation));
        Assert.False(MaintenancePlanService.IsWorkOrderPlanRestrictViolation(unique));

        var otherFk = new DbUpdateException(
            "conflict",
            new PostgresException(
                "violates foreign key constraint \"fk_units_tenants_tenant_id\"",
                "ERROR",
                "ERROR",
                PostgresErrorCodes.ForeignKeyViolation));
        Assert.False(MaintenancePlanService.IsWorkOrderPlanRestrictViolation(otherFk));

        var unrelated = new DbUpdateException("conflict", new InvalidOperationException("nope"));
        Assert.False(MaintenancePlanService.IsWorkOrderPlanRestrictViolation(unrelated));
    }

    private static MaintenancePlanService CreateService(BulkCreateAssetsHarness harness) =>
        new(harness.Db, harness.TenantProvider, harness.CreateRegistry());

    private static async Task<MaintenancePlanResponse> CreateCustomPlanAsync(
        BulkCreateAssetsHarness harness,
        MaintenancePlanService service,
        string name) =>
        await service.CreatePlanWithTasksAsync(Request(harness, name), CancellationToken.None);

    private static CreateMaintenancePlanRequest Request(
        BulkCreateAssetsHarness harness,
        string name,
        params CreatePlanTaskDto[] tasks) =>
        Request(harness, name, autoGenerateEnabled: false, omitAutoGenerate: true, tasks);

    private static CreateMaintenancePlanRequest Request(
        BulkCreateAssetsHarness harness,
        string name,
        bool autoGenerateEnabled,
        params CreatePlanTaskDto[] tasks) =>
        Request(harness, name, autoGenerateEnabled, omitAutoGenerate: false, tasks);

    private static CreateMaintenancePlanRequest Request(
        BulkCreateAssetsHarness harness,
        string name,
        bool autoGenerateEnabled,
        bool omitAutoGenerate,
        CreatePlanTaskDto[] tasks)
    {
        var request = new CreateMaintenancePlanRequest
        {
            UnitId = harness.UnitId,
            Name = name,
            Frequency = MaintenanceFrequency.Monthly,
            AssetCategoryId = harness.CategoryId,
            Tasks = tasks.Length == 0 ? [PlanTaskDto("Filtro", 1)] : [.. tasks],
        };

        return omitAutoGenerate ? request : request with { AutoGenerateEnabled = autoGenerateEnabled };
    }

    private static CreatePlanTaskDto PlanTaskDto(string title, int order, string? configuration = null) =>
        new()
        {
            Title = title,
            InputType = TaskInputType.Checkbox,
            Order = order,
            Configuration = configuration,
        };

    private static UpdateMaintenancePlanRequest HeaderUpdate(
        BulkCreateAssetsHarness harness,
        string name,
        bool isActive,
        bool autoGenerateEnabled) =>
        new()
        {
            UnitId = harness.UnitId,
            Name = name,
            Frequency = MaintenanceFrequency.Monthly,
            AssetCategoryId = harness.CategoryId,
            IsActive = isActive,
            AutoGenerateEnabled = autoGenerateEnabled,
        };

    private static async Task<WorkOrder> AddLinkedWorkOrderAsync(
        BulkCreateAssetsHarness harness,
        MaintenancePlanResponse plan,
        WorkOrderStatus status,
        string? snapshotValue,
        bool includeAllPlanTasks = false)
    {
        var asset = new Asset
        {
            TenantId = harness.TenantProvider.TenantId!.Value,
            UnitId = harness.UnitId,
            CategoryId = harness.CategoryId,
            FamilyId = harness.FamilyId,
            Name = "Split",
            Tag = $"AC-{Guid.NewGuid():N}"[..12],
            Status = AssetStatus.Active,
        };
        harness.Db.Assets.Add(asset);

        var workOrder = new WorkOrder
        {
            TenantId = asset.TenantId,
            AssetId = asset.Id,
            MaintenancePlanId = plan.Id,
            Status = status,
            ScheduledDate = new DateOnly(2026, 9, 18),
            SourcePlanName = plan.Name,
        };

        var sourceTasks = includeAllPlanTasks ? plan.Tasks : plan.Tasks.Take(1);
        foreach (var planTask in sourceTasks)
        {
            workOrder.AddTask(
                new WorkOrderTask
                {
                    TenantId = asset.TenantId,
                    WorkOrderId = workOrder.Id,
                    PlanTaskId = planTask.Id,
                    Title = planTask.Title,
                    InputType = planTask.InputType,
                    Configuration = planTask.Configuration,
                    IsMandatory = planTask.IsMandatory,
                    Order = planTask.Order,
                    Value = snapshotValue,
                });
        }

        harness.Db.WorkOrders.Add(workOrder);
        await harness.Db.SaveChangesAsync();
        return workOrder;
    }

    private static void AssertLineage(
        MaintenancePlanResponse plan,
        MaintenancePlanOriginKind originKind,
        Guid? sourceTemplateId,
        int? sourceTemplateVersion,
        bool autoGenerateEnabled)
    {
        Assert.Equal(originKind, plan.OriginKind);
        Assert.Equal(sourceTemplateId, plan.SourceTemplateId);
        Assert.Equal(sourceTemplateVersion, plan.SourceTemplateVersion);
        Assert.Equal(autoGenerateEnabled, plan.AutoGenerateEnabled);
    }

    private static (Guid Id, string Title, TaskInputType InputType, string? Configuration, bool IsMandatory, int Order, string? Value)
        ImmutableSnapshot(WorkOrderTask task) =>
        (task.Id, task.Title, task.InputType, task.Configuration, task.IsMandatory, task.Order, task.Value);
}
