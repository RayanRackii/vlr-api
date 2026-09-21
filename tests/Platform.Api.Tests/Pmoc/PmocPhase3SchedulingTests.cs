using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Pmoc.Dtos;
using Platform.Api.Modules.Pmoc.Services;
using Platform.Api.Tests.Assets;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.Pmoc;

public sealed class PmocPhase3SchedulingTests
{
    [Fact]
    public async Task Create_requires_and_returns_interval_fields()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var created = await CreateService(harness).CreatePlanWithTasksAsync(
            Request(harness, "Interval", 30, new DateOnly(2026, 1, 1)),
            CancellationToken.None);

        Assert.Equal(30, created.IntervalDays);
        Assert.Equal(new DateOnly(2026, 1, 1), created.FirstDueDate);
        Assert.Null(typeof(MaintenancePlanResponse).GetProperty("Frequency"));
        Assert.Null(typeof(MaintenancePlanResponse).GetProperty("ScheduleMode"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3651)]
    public async Task Create_rejects_interval_days_outside_range(int intervalDays)
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateService(harness).CreatePlanWithTasksAsync(
                Request(harness, "Bad", intervalDays, new DateOnly(2026, 9, 19)),
                CancellationToken.None));

        Assert.Contains("IntervalDays", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PmocScheduling.MinIntervalDays)]
    [InlineData(PmocScheduling.MaxIntervalDays)]
    public async Task Create_accepts_interval_bounds(int intervalDays)
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var created = await CreateService(harness).CreatePlanWithTasksAsync(
            Request(harness, $"Bound-{intervalDays}", intervalDays, new DateOnly(2026, 9, 19)),
            CancellationToken.None);

        Assert.Equal(intervalDays, created.IntervalDays);
    }

    [Fact]
    public async Task Past_today_and_future_first_due_dates_are_accepted()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var today = BrazilTimeZone.GetToday();
        var service = CreateService(harness);

        var past = await service.CreatePlanWithTasksAsync(
            Request(harness, "Past", 30, today.AddDays(-10)),
            CancellationToken.None);
        var same = await service.CreatePlanWithTasksAsync(
            Request(harness, "Today", 30, today),
            CancellationToken.None);
        var future = await service.CreatePlanWithTasksAsync(
            Request(harness, "Future", 30, today.AddDays(10)),
            CancellationToken.None);

        Assert.Equal(today.AddDays(-10), past.FirstDueDate);
        Assert.Equal(today, same.FirstDueDate);
        Assert.Equal(today.AddDays(10), future.FirstDueDate);
    }

    [Fact]
    public async Task Update_changes_interval_and_first_due_without_touching_tasks()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var service = CreateService(harness);
        var created = await service.CreatePlanWithTasksAsync(
            Request(harness, "Editable", 30, new DateOnly(2026, 10, 1)),
            CancellationToken.None);
        var taskId = Assert.Single(created.Tasks).Id;

        var updated = await service.UpdateAsync(
            created.Id,
            new UpdateMaintenancePlanRequest
            {
                UnitId = harness.UnitId,
                Name = "Editable",
                IntervalDays = 60,
                FirstDueDate = new DateOnly(2026, 10, 20),
                AssetCategoryId = harness.CategoryId,
                IsActive = true,
                AutoGenerateEnabled = false,
            },
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(60, updated!.IntervalDays);
        Assert.Equal(new DateOnly(2026, 10, 20), updated.FirstDueDate);
        Assert.Equal(taskId, Assert.Single(updated.Tasks).Id);
        Assert.Equal("Filtro", updated.Tasks[0].Title);
    }

    [Fact]
    public async Task From_template_uses_tenant_scheduling_not_template_fields()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var template = new GlobalMaintenanceTemplate
        {
            Name = "Clone source",
            Description = "Checklist only",
            Jurisdiction = "BR",
            TargetEquipmentType = "Ar Condicionado",
            LibraryKey = "pmoc-phase3-clone-source",
            Version = 1,
            Status = GlobalTemplateStatus.Published,
        };
        template.AddTask(new GlobalTemplateTask
        {
            GlobalMaintenanceTemplateId = template.Id,
            Title = "Filtro",
            InputType = TaskInputType.Checkbox,
            IsMandatory = true,
            Order = 1,
        });
        harness.Db.GlobalMaintenanceTemplates.Add(template);
        await harness.Db.SaveChangesAsync();
        var service = CreateService(harness);

        var created = await service.CreateFromTemplateAsync(
            new CreateFromTemplateRequest
            {
                TemplateId = template.Id,
                UnitId = harness.UnitId,
                AssetCategoryId = harness.CategoryId,
                IntervalDays = 45,
                FirstDueDate = new DateOnly(2026, 8, 1),
            },
            CancellationToken.None);

        Assert.Equal(45, created.IntervalDays);
        Assert.Equal(new DateOnly(2026, 8, 1), created.FirstDueDate);
        Assert.Equal(MaintenancePlanOriginKind.RolvixTemplate, created.OriginKind);
        Assert.Equal(template.Id, created.SourceTemplateId);
        Assert.Equal("Filtro", Assert.Single(created.Tasks).Title);
        Assert.Null(typeof(GlobalMaintenanceTemplate).GetProperty("IntervalDays"));
        Assert.Null(typeof(GlobalMaintenanceTemplate).GetProperty("FirstDueDate"));
        Assert.Null(typeof(GlobalMaintenanceTemplate).GetProperty("Frequency"));
    }

    private static MaintenancePlanService CreateService(BulkCreateAssetsHarness harness) =>
        new(harness.Db, harness.TenantProvider, harness.CreateRegistry());

    private static CreateMaintenancePlanRequest Request(
        BulkCreateAssetsHarness harness,
        string name,
        int intervalDays,
        DateOnly firstDueDate) =>
        new()
        {
            UnitId = harness.UnitId,
            Name = name,
            IntervalDays = intervalDays,
            FirstDueDate = firstDueDate,
            AssetCategoryId = harness.CategoryId,
            Tasks =
            [
                new CreatePlanTaskDto
                {
                    Title = "Filtro",
                    InputType = TaskInputType.Checkbox,
                    Order = 1,
                },
            ],
        };
}
