using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Authorization;
using Platform.Api.Modules.Pmoc.Services;
using Platform.Api.Modules.Users.Controllers;
using Platform.Api.Modules.WorkOrders;
using Platform.Api.Modules.WorkOrders.Controllers;
using Platform.Api.Modules.WorkOrders.Dtos;
using Platform.Api.Modules.WorkOrders.Services;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.WorkOrders;

public sealed class WorkOrderIntegrityTests
{
    [Fact]
    public async Task Task_value_on_completed_or_canceled_order_is_rejected_and_unchanged()
    {
        await using var harness = await WorkOrderRbacHarness.CreateAsync();
        var completed = AddTask(harness, harness.AddWorkOrder(harness.Executor.Id), TaskInputType.Text, null, true);
        completed.WorkOrder.Status = WorkOrderStatus.Completed;
        completed.WorkOrder.CompletedDate = DateTimeOffset.Parse("2026-10-01T12:00:00Z");
        var canceled = AddTask(harness, harness.AddWorkOrder(harness.Executor.Id), TaskInputType.Text, null, true);
        canceled.WorkOrder.Status = WorkOrderStatus.Canceled;
        await harness.Db.SaveChangesAsync();
        harness.SetUser(harness.Executor);

        var closed = await Assert.ThrowsAsync<WorkOrderClosedException>(() =>
            harness.Service.UpdateTaskValueAsync(
                completed.WorkOrder.Id,
                completed.Id,
                new UpdateWorkOrderTaskValueRequest { Value = "later" },
                CancellationToken.None));
        var alsoClosed = await Assert.ThrowsAsync<WorkOrderClosedException>(() =>
            harness.Service.UpdateTaskValueAsync(
                canceled.WorkOrder.Id,
                canceled.Id,
                new UpdateWorkOrderTaskValueRequest { Value = "later" },
                CancellationToken.None));

        Assert.Equal("WORK_ORDER_CLOSED", closed.Code);
        Assert.Equal(WorkOrderErrorCodes.Closed, alsoClosed.Code);
        Assert.Null(await harness.Db.WorkOrderTasks.Where(task => task.Id == completed.Id).Select(task => task.Value).SingleAsync());
        Assert.Null(await harness.Db.WorkOrderTasks.Where(task => task.Id == canceled.Id).Select(task => task.Value).SingleAsync());
    }

    [Fact]
    public async Task Task_values_are_validated_by_input_type()
    {
        await using var harness = await WorkOrderRbacHarness.CreateAsync();
        var number = AddTask(harness, harness.AddWorkOrder(harness.Executor.Id), TaskInputType.Number, """{"min":0,"max":10}""", true);
        var choice = AddTask(harness, harness.AddWorkOrder(harness.Executor.Id), TaskInputType.SingleChoice, """{"options":["Íntegro","Danificado"]}""", true);
        var checkbox = AddTask(harness, harness.AddWorkOrder(harness.Executor.Id), TaskInputType.Checkbox, null, true);
        var text = AddTask(harness, harness.AddWorkOrder(harness.Executor.Id), TaskInputType.Text, """{"maxLength":3}""", true);
        await harness.Db.SaveChangesAsync();
        harness.SetUser(harness.Executor);

        var range = await Assert.ThrowsAsync<InvalidTaskValueException>(() =>
            Update(harness, number, "11"));
        var option = await Assert.ThrowsAsync<InvalidTaskValueException>(() =>
            Update(harness, choice, "Outro"));
        var box = await Assert.ThrowsAsync<InvalidTaskValueException>(() =>
            Update(harness, checkbox, "maybe"));
        var length = await Assert.ThrowsAsync<InvalidTaskValueException>(() =>
            Update(harness, text, "abcd"));
        var stored = await Update(harness, number, "5");

        Assert.Equal(InvalidTaskValueException.OutOfRange, range.Reason);
        Assert.Equal(InvalidTaskValueException.NotAnOption, option.Reason);
        Assert.Equal(InvalidTaskValueException.InvalidCheckbox, box.Reason);
        Assert.Equal(InvalidTaskValueException.TextTooLong, length.Reason);
        Assert.NotNull(stored);
        Assert.Equal("5", stored.Tasks.Single(task => task.Id == number.Id).Value);
    }

    [Fact]
    public async Task Mandatory_checkbox_false_blocks_completion_with_task_ids()
    {
        await using var harness = await WorkOrderRbacHarness.CreateAsync();
        var order = harness.AddWorkOrder(harness.Executor.Id);
        var required = AddTask(harness, order, TaskInputType.Checkbox, null, isMandatory: true, title: "Filtro");
        var optional = AddTask(harness, order, TaskInputType.Checkbox, null, isMandatory: false, title: "Opcional");
        await harness.Db.SaveChangesAsync();
        harness.SetUser(harness.Executor);
        await Update(harness, required, "false");
        await Update(harness, optional, "false");

        var missing = await Assert.ThrowsAsync<MandatoryTasksIncompleteException>(() =>
            harness.Service.UpdateStatusAsync(
                order.Id,
                new UpdateWorkOrderStatusRequest { Status = WorkOrderStatus.Completed },
                CancellationToken.None));

        Assert.Equal("MANDATORY_TASKS_INCOMPLETE", missing.Code);
        var listed = Assert.Single(missing.Tasks);
        Assert.Equal(required.Id, listed.Id);
        Assert.Equal("Filtro", listed.Title);
        Assert.Equal(WorkOrderStatus.Pending, (await harness.Db.WorkOrders.SingleAsync(item => item.Id == order.Id)).Status);
    }

    [Fact]
    public async Task Non_mandatory_checkbox_false_can_complete()
    {
        await using var harness = await WorkOrderRbacHarness.CreateAsync();
        var order = harness.AddWorkOrder(harness.Executor.Id);
        var optional = AddTask(harness, order, TaskInputType.Checkbox, null, isMandatory: false);
        await harness.Db.SaveChangesAsync();
        harness.SetUser(harness.Executor);
        await Update(harness, optional, "false");

        var completed = await harness.Service.UpdateStatusAsync(
            order.Id,
            new UpdateWorkOrderStatusRequest { Status = WorkOrderStatus.Completed },
            CancellationToken.None);

        Assert.NotNull(completed);
        Assert.Equal(WorkOrderStatus.Completed, completed.Status);
        Assert.Equal("false", completed.Tasks.Single().Value);
    }

    [Fact]
    public async Task Reopen_is_rejected_and_cancel_clears_completed_date()
    {
        await using var harness = await WorkOrderRbacHarness.CreateAsync();
        var order = harness.AddWorkOrder(harness.Executor.Id);
        AddTask(harness, order, TaskInputType.Text, null, true);
        order.Status = WorkOrderStatus.Completed;
        var completedAt = DateTimeOffset.Parse("2026-10-02T15:00:00Z");
        order.CompletedDate = completedAt;
        await harness.Db.SaveChangesAsync();
        harness.SetUser(harness.Creator);
        var controller = new WorkOrdersController(harness.Service, null!, null!);

        var reopen = await controller.UpdateStatus(
            order.Id,
            new UpdateWorkOrderStatusRequest { Status = WorkOrderStatus.Pending },
            CancellationToken.None);
        var conflict = Assert.IsType<ConflictObjectResult>(reopen.Result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(conflict.Value));
        Assert.Equal("WORK_ORDER_REOPEN_NOT_ALLOWED", doc.RootElement.GetProperty("code").GetString());

        var stored = await harness.Db.WorkOrders.SingleAsync(item => item.Id == order.Id);
        Assert.Equal(WorkOrderStatus.Completed, stored.Status);
        Assert.Equal(completedAt, stored.CompletedDate);

        var canceled = await harness.Service.CancelAsync(order.Id, CancellationToken.None);
        Assert.NotNull(canceled);
        Assert.Equal(WorkOrderStatus.Canceled, canceled.Status);
        Assert.Null(canceled.CompletedDate);

        var again = await Assert.ThrowsAsync<WorkOrderClosedException>(() =>
            harness.Service.CancelAsync(order.Id, CancellationToken.None));
        Assert.Equal(WorkOrderErrorCodes.Closed, again.Code);
    }

    [Fact]
    public async Task Execute_only_cannot_cancel_another_users_order_and_creator_can()
    {
        await using var harness = await WorkOrderRbacHarness.CreateAsync();
        var foreign = harness.AddWorkOrder(harness.Creator.Id);
        foreign.Status = WorkOrderStatus.InProgress;
        await harness.Db.SaveChangesAsync();
        harness.SetUser(harness.Executor);

        Assert.Null(await harness.Service.CancelAsync(foreign.Id, CancellationToken.None));

        harness.SetUser(harness.Creator);
        var canceled = await harness.Service.CancelAsync(foreign.Id, CancellationToken.None);
        Assert.NotNull(canceled);
        Assert.Equal(WorkOrderStatus.Canceled, canceled.Status);
        Assert.Null(canceled.CompletedDate);
    }

    [Fact]
    public async Task Assignee_must_be_able_to_execute_and_null_clears_it()
    {
        await using var harness = await WorkOrderRbacHarness.CreateAsync();
        var order = harness.AddWorkOrder(harness.Executor.Id);
        await harness.Db.SaveChangesAsync();
        harness.SetUser(harness.Creator);

        var rejected = await Assert.ThrowsAsync<AssigneeCannotExecuteException>(() =>
            harness.Service.UpdateAssigneeAsync(order.Id, harness.Creator.Id, CancellationToken.None));
        Assert.Contains(harness.Creator.Id.ToString(), rejected.Message, StringComparison.Ordinal);

        var cleared = await harness.Service.UpdateAssigneeAsync(order.Id, null, CancellationToken.None);
        Assert.NotNull(cleared);
        Assert.Null(cleared.AssignedUserId);
    }

    [Fact]
    public async Task Create_with_plan_matches_from_plan_checks_and_duplicate_is_409()
    {
        await using var harness = await WorkOrderRbacHarness.CreateAsync();
        var unit = await harness.Db.Units.SingleAsync();
        var category = await harness.Db.AssetCategories.SingleAsync();
        var asset = await harness.Db.Assets.SingleAsync();
        var inactive = Plan(harness, unit.Id, category.Id, "Inativo", isActive: false);
        var active = Plan(harness, unit.Id, category.Id, "Ativo", isActive: true);
        harness.Db.MaintenancePlans.AddRange(inactive, active);
        await harness.Db.SaveChangesAsync();
        harness.SetUser(harness.Creator);
        var date = new DateOnly(2026, 10, 17);

        var inactiveError = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Service.CreateAsync(Request(asset.Id, inactive.Id, date), CancellationToken.None));
        Assert.Equal("Maintenance plan is not active.", inactiveError.Message);

        asset.ScheduledDeletionAt = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
        await harness.Db.SaveChangesAsync();
        var ineligible = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Service.CreateAsync(Request(asset.Id, active.Id, date), CancellationToken.None));
        Assert.Contains("not eligible", ineligible.Message, StringComparison.OrdinalIgnoreCase);

        var adHoc = await harness.Service.CreateAsync(Request(asset.Id, planId: null, date), CancellationToken.None);
        Assert.Null(adHoc.MaintenancePlanId);

        asset.ScheduledDeletionAt = null;
        await harness.Db.SaveChangesAsync();
        var created = await harness.Service.CreateAsync(Request(asset.Id, active.Id, date), CancellationToken.None);
        Assert.Equal(active.Id, created.MaintenancePlanId);
        Assert.Equal(active.Name, created.SourcePlanName);

        await Assert.ThrowsAsync<DuplicateWorkOrderException>(() =>
            harness.Service.CreateAsync(Request(asset.Id, active.Id, date), CancellationToken.None));
        var controller = new WorkOrdersController(harness.Service, null!, null!);
        var result = await controller.Create(Request(asset.Id, active.Id, date), CancellationToken.None);
        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(conflict.Value));
        Assert.Equal(DuplicateWorkOrderException.Code, doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Canceling_a_completed_plan_order_returns_coverage_to_never_executed()
    {
        await using var harness = await WorkOrderRbacHarness.CreateAsync();
        var unit = await harness.Db.Units.SingleAsync();
        var category = await harness.Db.AssetCategories.SingleAsync();
        var asset = await harness.Db.Assets.SingleAsync();
        var plan = Plan(harness, unit.Id, category.Id, "Cobertura", isActive: true);
        plan.FirstDueDate = new DateOnly(2026, 10, 1);
        harness.Db.MaintenancePlans.Add(plan);
        var order = harness.AddWorkOrder(harness.Executor.Id);
        order.MaintenancePlanId = plan.Id;
        order.Status = WorkOrderStatus.Completed;
        order.CompletedDate = DateTimeOffset.Parse("2026-10-02T12:00:00Z");
        await harness.Db.SaveChangesAsync();
        harness.SetUser(harness.Creator);

        await harness.Service.CancelAsync(order.Id, CancellationToken.None);
        var coverage = await new MaintenancePlanCoverageService(
                harness.Db,
                harness.TenantProvider,
                new TestTimeProvider(BrazilTimeZone.AtLocal(new DateOnly(2026, 10, 17), new TimeOnly(12, 0))))
            .GetCoverageAsync(plan.Id, CancellationToken.None);

        Assert.NotNull(coverage);
        var row = Assert.Single(coverage.Assets);
        Assert.Equal(asset.Id, row.AssetId);
        Assert.Equal(PmocHistoryStatus.NeverExecuted, row.HistoryStatus);
        Assert.Null(row.LastMaintenance);
    }

    [Fact]
    public void Cancel_assignee_and_technicians_permissions_match_the_contract()
    {
        var cancel = typeof(WorkOrdersController).GetMethod(nameof(WorkOrdersController.Cancel));
        Assert.Equal(
            PermissionPolicies.AnyName(Permissions.Os.WorkOrdersExecute, Permissions.Os.WorkOrdersCreate),
            cancel!.GetCustomAttribute<RequireAnyPermissionAttribute>()!.Policy);

        var assignee = typeof(WorkOrdersController).GetMethod(nameof(WorkOrdersController.UpdateAssignee));
        Assert.Equal(
            PermissionPolicies.Name(Permissions.Os.WorkOrdersAssign),
            assignee!.GetCustomAttribute<RequirePermissionAttribute>()!.Policy);

        var technicians = typeof(UsersController).GetMethod(nameof(UsersController.ListTechnicians));
        Assert.Equal(
            PermissionPolicies.AnyName(
                Permissions.Core.UsersRead,
                Permissions.Os.WorkOrdersCreate,
                Permissions.Os.WorkOrdersAssign),
            technicians!.GetCustomAttribute<RequireAnyPermissionAttribute>()!.Policy);
    }

    private static Task<WorkOrderResponse?> Update(
        WorkOrderRbacHarness harness,
        WorkOrderTask task,
        string value) =>
        harness.Service.UpdateTaskValueAsync(
            task.WorkOrderId,
            task.Id,
            new UpdateWorkOrderTaskValueRequest { Value = value },
            CancellationToken.None);

    private static WorkOrderTask AddTask(
        WorkOrderRbacHarness harness,
        WorkOrder order,
        TaskInputType inputType,
        string? configuration,
        bool isMandatory,
        string title = "Tarefa")
    {
        var task = new WorkOrderTask
        {
            TenantId = harness.TenantProvider.TenantId!.Value,
            WorkOrderId = order.Id,
            Title = title,
            InputType = inputType,
            Configuration = configuration,
            IsMandatory = isMandatory,
            Order = order.Tasks.Count + 1,
        };
        task.WorkOrder = order;
        order.AddTask(task);
        return task;
    }

    private static MaintenancePlan Plan(
        WorkOrderRbacHarness harness,
        Guid unitId,
        Guid categoryId,
        string name,
        bool isActive) =>
        new()
        {
            TenantId = harness.TenantProvider.TenantId!.Value,
            UnitId = unitId,
            Name = name,
            IntervalDays = 30,
            FirstDueDate = new DateOnly(2026, 10, 1),
            AssetCategoryId = categoryId,
            IsActive = isActive,
        };

    private static CreateWorkOrderRequest Request(Guid assetId, Guid? planId, DateOnly date) =>
        new()
        {
            AssetId = assetId,
            MaintenancePlanId = planId,
            ScheduledDate = date,
            Tasks =
            [
                new CreateWorkOrderTaskDto
                {
                    Title = "Inspeção",
                    InputType = TaskInputType.Text,
                    Order = 1,
                    IsMandatory = false,
                },
            ],
        };
}
