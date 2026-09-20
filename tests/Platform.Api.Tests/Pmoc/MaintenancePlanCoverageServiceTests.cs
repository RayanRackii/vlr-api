using Microsoft.EntityFrameworkCore;
using Platform.Api.Jobs;
using Platform.Api.Modules.Pmoc.Services;
using Platform.Api.Tests.Assets;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.Pmoc;

public sealed class MaintenancePlanCoverageServiceTests
{
    private static readonly DateOnly AsOf = new(2026, 9, 19);
    private static readonly DateOnly FutureFirstDue = new(2026, 10, 1);

    [Fact]
    public async Task Eligible_active_matching_asset_is_included()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: FutureFirstDue);
        var asset = await AddAssetAsync(harness, tag: "AC-01");

        var coverage = await GetCoverageAsync(harness, plan.Id);

        var row = Assert.Single(coverage.Assets);
        Assert.Equal(asset.Id, row.AssetId);
        Assert.Equal("AC-01", row.Tag);
        Assert.Equal(PmocHistoryStatus.NeverExecuted, row.HistoryStatus);
        Assert.Equal(PmocDueStatus.NotDue, row.DueStatus);
        Assert.False(row.NeedsAttention);
        Assert.Equal(1, coverage.Summary.EligibleAssets);
        Assert.Equal(coverage.Summary.EligibleAssets, coverage.EligibleAssetCount);
        Assert.Equal(
            coverage.Summary.EligibleAssets,
            coverage.Summary.AssetsNeverExecuted + coverage.Summary.AssetsExecuted);
        Assert.False(coverage.WouldBeConsideredByGenerator);
    }

    [Fact]
    public async Task Inactive_asset_is_excluded()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness);
        await AddAssetAsync(harness, tag: "OFF", status: AssetStatus.Inactive);

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.Empty(coverage.Assets);
        Assert.Equal(0, coverage.Summary.EligibleAssets);
    }

    [Fact]
    public async Task Maintenance_status_asset_is_excluded()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness);
        await AddAssetAsync(harness, tag: "MNT", status: AssetStatus.Maintenance);

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.Empty(coverage.Assets);
    }

    [Fact]
    public async Task Scheduled_deletion_asset_is_excluded()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness);
        await AddAssetAsync(harness, tag: "DEL", scheduledDeletionAt: DateTimeOffset.UtcNow);

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.Empty(coverage.Assets);
    }

    [Fact]
    public async Task Wrong_unit_asset_is_excluded()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness);
        var otherUnit = new Unit(harness.TenantProvider.TenantId!.Value, "Other");
        harness.Db.Units.Add(otherUnit);
        await harness.Db.SaveChangesAsync();
        await AddAssetAsync(harness, tag: "U2", unitId: otherUnit.Id);

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.Empty(coverage.Assets);
    }

    [Fact]
    public async Task Wrong_category_asset_is_excluded()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness);
        var otherCategory = new AssetCategory
        {
            TenantId = harness.TenantProvider.TenantId!.Value,
            Name = "Other cat",
        };
        harness.Db.AssetCategories.Add(otherCategory);
        await harness.Db.SaveChangesAsync();
        await AddAssetAsync(harness, tag: "C2", categoryId: otherCategory.Id);

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.Empty(coverage.Assets);
    }

    [Fact]
    public async Task Requires_maintenance_false_does_not_exclude()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: FutureFirstDue);
        var withoutFlag = await AddAssetAsync(harness, tag: "NO-FLAG", requiresMaintenance: false);
        var withFlag = await AddAssetAsync(harness, tag: "FLAG", requiresMaintenance: true);

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.Equal(2, coverage.Summary.EligibleAssets);
        Assert.Contains(coverage.Assets, row => row.AssetId == withoutFlag.Id);
        Assert.Contains(coverage.Assets, row => row.AssetId == withFlag.Id);
    }

    [Fact]
    public async Task Pending_work_order_does_not_reset_clock()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: FutureFirstDue);
        var asset = await AddAssetAsync(harness, tag: "NEVER");
        await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.Pending,
            scheduledDate: AsOf);

        var coverage = await GetCoverageAsync(harness, plan.Id);

        var row = Assert.Single(coverage.Assets);
        Assert.Equal(PmocHistoryStatus.NeverExecuted, row.HistoryStatus);
        Assert.Equal(PmocDueStatus.NotDue, row.DueStatus);
        Assert.Equal(FutureFirstDue, row.NextDueDate);
        Assert.Null(row.LastMaintenance);
        Assert.NotNull(row.OpenWorkOrder);
        Assert.Equal(1, coverage.Summary.AssetsNeverExecuted);
        Assert.Equal(0, coverage.Summary.AssetsWithPmocHistory);
    }

    [Fact]
    public async Task Completed_work_order_advances_next_due_from_brazil_civil_date()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, intervalDays: 30);
        var asset = await AddAssetAsync(harness, tag: "OK");
        var completed = await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.Completed,
            scheduledDate: new DateOnly(2026, 9, 1),
            completedDate: BrazilTimeZone.AtLocal(new DateOnly(2026, 9, 2), new TimeOnly(12, 0)));

        var coverage = await GetCoverageAsync(harness, plan.Id);

        var row = Assert.Single(coverage.Assets);
        Assert.Equal(PmocHistoryStatus.Executed, row.HistoryStatus);
        Assert.Equal(PmocDueStatus.NotDue, row.DueStatus);
        Assert.Equal(new DateOnly(2026, 10, 2), row.NextDueDate);
        Assert.Equal(completed.Id, row.LastMaintenance!.WorkOrderId);
        Assert.Equal(1, coverage.Summary.AssetsExecuted);
        Assert.False(row.NeedsAttention);
    }

    [Fact]
    public async Task Executed_asset_is_overdue_when_interval_has_elapsed()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, intervalDays: 30);
        var asset = await AddAssetAsync(harness, tag: "LATE");
        await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.Completed,
            scheduledDate: new DateOnly(2026, 8, 1),
            completedDate: BrazilTimeZone.AtLocal(new DateOnly(2026, 8, 2), new TimeOnly(12, 0)));

        var coverage = await GetCoverageAsync(harness, plan.Id);

        var row = Assert.Single(coverage.Assets);
        Assert.Equal(PmocHistoryStatus.Executed, row.HistoryStatus);
        Assert.Equal(PmocDueStatus.Overdue, row.DueStatus);
        Assert.True(row.NeedsAttention);
        Assert.Equal(new DateOnly(2026, 9, 1), row.NextDueDate);
        Assert.Equal(1, coverage.Summary.AssetsOverdue);
        Assert.Equal(1, coverage.Summary.AssetsWithPmocHistory);
    }

    [Fact]
    public async Task Canceled_work_order_is_ignored()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: FutureFirstDue);
        var asset = await AddAssetAsync(harness, tag: "CAN");
        await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.Canceled,
            scheduledDate: new DateOnly(2026, 9, 1),
            completedDate: BrazilTimeZone.AtLocal(new DateOnly(2026, 9, 2), new TimeOnly(12, 0)));

        var coverage = await GetCoverageAsync(harness, plan.Id);

        var row = Assert.Single(coverage.Assets);
        Assert.Equal(PmocHistoryStatus.NeverExecuted, row.HistoryStatus);
        Assert.Null(row.LastMaintenance);
        Assert.Null(row.OpenWorkOrder);
    }

    [Fact]
    public async Task Manual_work_order_without_plan_is_ignored()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: FutureFirstDue);
        var asset = await AddAssetAsync(harness, tag: "MAN");
        await AddWorkOrderAsync(
            harness,
            maintenancePlanId: null,
            asset.Id,
            WorkOrderStatus.Completed,
            scheduledDate: new DateOnly(2026, 9, 1),
            completedDate: BrazilTimeZone.AtLocal(new DateOnly(2026, 9, 2), new TimeOnly(12, 0)));

        var coverage = await GetCoverageAsync(harness, plan.Id);

        var row = Assert.Single(coverage.Assets);
        Assert.Equal(PmocHistoryStatus.NeverExecuted, row.HistoryStatus);
        Assert.Null(row.LastMaintenance);
        Assert.Equal(FutureFirstDue, row.NextDueDate);
    }

    [Fact]
    public async Task Last_maintenance_uses_latest_completed_date_then_scheduled_date_then_id()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness);
        var asset = await AddAssetAsync(harness, tag: "HIST");
        await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.Completed,
            scheduledDate: new DateOnly(2026, 9, 15),
            completedDate: BrazilTimeZone.AtLocal(new DateOnly(2026, 9, 10), new TimeOnly(12, 0)));
        var newer = await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.Completed,
            scheduledDate: new DateOnly(2026, 9, 1),
            completedDate: BrazilTimeZone.AtLocal(new DateOnly(2026, 9, 18), new TimeOnly(12, 0)));

        var coverage = await GetCoverageAsync(harness, plan.Id);

        var row = Assert.Single(coverage.Assets);
        Assert.Equal(newer.Id, row.LastMaintenance!.WorkOrderId);
        Assert.Equal(new DateOnly(2026, 10, 18), row.NextDueDate);
    }

    [Fact]
    public async Task Last_maintenance_id_desc_breaks_equal_completed_and_scheduled()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness);
        var asset = await AddAssetAsync(harness, tag: "TIE");
        var completed = BrazilTimeZone.AtLocal(new DateOnly(2026, 9, 10), new TimeOnly(12, 0));
        var first = await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.Completed,
            scheduledDate: new DateOnly(2026, 9, 1),
            completedDate: completed);
        var second = await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.Completed,
            scheduledDate: new DateOnly(2026, 9, 1),
            completedDate: completed);
        var expectedId = first.Id.CompareTo(second.Id) > 0 ? first.Id : second.Id;

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.Equal(expectedId, Assert.Single(coverage.Assets).LastMaintenance!.WorkOrderId);
    }

    [Fact]
    public async Task Open_work_order_prefers_in_progress_over_pending_regardless_of_date()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: FutureFirstDue);
        var asset = await AddAssetAsync(harness, tag: "OPEN");
        var pendingEarlier = await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.Pending,
            scheduledDate: new DateOnly(2026, 9, 1));
        var inProgressLater = await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.InProgress,
            scheduledDate: new DateOnly(2026, 10, 1));
        _ = pendingEarlier;

        var coverage = await GetCoverageAsync(harness, plan.Id);

        var open = Assert.Single(coverage.Assets).OpenWorkOrder;
        Assert.NotNull(open);
        Assert.Equal(inProgressLater.Id, open!.WorkOrderId);
        Assert.Equal(WorkOrderStatus.InProgress, open.Status);
        Assert.Equal(1, coverage.Summary.AssetsWithOpenWorkOrder);
    }

    [Fact]
    public async Task Newly_eligible_never_executed_asset_is_overdue_against_past_first_due()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: new DateOnly(2026, 9, 1));
        var asset = await AddAssetAsync(harness, tag: "NEW");

        var coverage = await GetCoverageAsync(harness, plan.Id);

        var row = Assert.Single(coverage.Assets);
        Assert.Equal(asset.Id, row.AssetId);
        Assert.Equal(PmocHistoryStatus.NeverExecuted, row.HistoryStatus);
        Assert.Equal(PmocDueStatus.Overdue, row.DueStatus);
        Assert.True(row.NeedsAttention);
        Assert.Equal(new DateOnly(2026, 9, 1), row.NextDueDate);
    }

    [Fact]
    public async Task Summary_counts_history_and_due_independently()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: FutureFirstDue, intervalDays: 30);
        var never = await AddAssetAsync(harness, tag: "Z-NEVER");
        var overdue = await AddAssetAsync(harness, tag: "A-LATE");
        var notDue = await AddAssetAsync(harness, tag: "M-OK");
        await AddWorkOrderAsync(
            harness,
            plan.Id,
            overdue.Id,
            WorkOrderStatus.Completed,
            scheduledDate: new DateOnly(2026, 8, 1),
            completedDate: BrazilTimeZone.AtLocal(new DateOnly(2026, 8, 2), new TimeOnly(12, 0)));
        await AddWorkOrderAsync(
            harness,
            plan.Id,
            notDue.Id,
            WorkOrderStatus.Completed,
            scheduledDate: new DateOnly(2026, 9, 1),
            completedDate: BrazilTimeZone.AtLocal(new DateOnly(2026, 9, 2), new TimeOnly(12, 0)));
        _ = never;

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.Equal(3, coverage.Summary.EligibleAssets);
        Assert.Equal(1, coverage.Summary.AssetsNeverExecuted);
        Assert.Equal(2, coverage.Summary.AssetsExecuted);
        Assert.Equal(1, coverage.Summary.AssetsOverdue);
        Assert.Equal(2, coverage.Summary.AssetsNotDue);
        Assert.Equal(1, coverage.Summary.AssetsNeedingAttention);
        Assert.Equal(overdue.Id, coverage.Assets[0].AssetId);
        Assert.All(coverage.Assets, row => Assert.Equal(coverage.AsOfDate, AsOf));
        Assert.False(coverage.WouldBeConsideredByGenerator);
    }

    [Fact]
    public async Task Generator_visibility_is_false_while_job_is_fail_closed()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(
            harness,
            isActive: true,
            autoGenerateEnabled: true,
            firstDueDate: AsOf);

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.True(plan.IsActive);
        Assert.True(plan.AutoGenerateEnabled);
        Assert.False(coverage.WouldBeConsideredByGenerator);
        Assert.Equal(0, coverage.Summary.EligibleAssets);
        Assert.Equal(30, coverage.IntervalDays);
        Assert.Equal(AsOf, coverage.FirstDueDate);
    }

    [Fact]
    public async Task Missing_plan_returns_null()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();

        var coverage = await CreateService(harness)
            .GetCoverageAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(coverage);
    }

    [Fact]
    public async Task Foreign_tenant_plan_returns_null()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness);
        var originalTenant = harness.TenantProvider.TenantId;
        harness.TenantProvider.TenantId = Guid.NewGuid();

        var coverage = await CreateService(harness)
            .GetCoverageAsync(plan.Id, CancellationToken.None);

        Assert.Null(coverage);
        harness.TenantProvider.TenantId = originalTenant;
    }

    [Fact]
    public async Task Many_assets_are_joined_in_memory_without_per_asset_round_trips()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: FutureFirstDue);
        for (var i = 0; i < 20; i++)
        {
            var asset = await AddAssetAsync(harness, tag: $"AC-{i:00}");
            await AddWorkOrderAsync(
                harness,
                plan.Id,
                asset.Id,
                WorkOrderStatus.Pending,
                scheduledDate: AsOf);
        }

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.Equal(20, coverage.Summary.EligibleAssets);
        Assert.Equal(20, coverage.Assets.Count);
        Assert.Equal(20, coverage.Summary.AssetsWithOpenWorkOrder);
        Assert.All(coverage.Assets, row => Assert.Equal(PmocHistoryStatus.NeverExecuted, row.HistoryStatus));
    }

    [Fact]
    public async Task Completed_without_completed_date_throws()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness);
        var asset = await AddAssetAsync(harness, tag: "BAD");
        await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.Completed,
            scheduledDate: AsOf,
            completedDate: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GetCoverageAsync(harness, plan.Id));
        Assert.Contains("CompletedDate", ex.Message, StringComparison.Ordinal);
    }

    private static MaintenancePlanCoverageService CreateService(BulkCreateAssetsHarness harness) =>
        new(
            harness.Db,
            harness.TenantProvider,
            new TestTimeProvider(BrazilTimeZone.AtLocal(AsOf, new TimeOnly(12, 0))));

    private static async Task<Platform.Api.Modules.Pmoc.Dtos.MaintenancePlanCoverageResponse> GetCoverageAsync(
        BulkCreateAssetsHarness harness,
        Guid planId)
    {
        var coverage = await CreateService(harness)
            .GetCoverageAsync(planId, CancellationToken.None);
        Assert.NotNull(coverage);
        return coverage!;
    }

    private static async Task<MaintenancePlan> SeedPlanAsync(
        BulkCreateAssetsHarness harness,
        int intervalDays = 30,
        DateOnly? firstDueDate = null,
        bool isActive = true,
        bool autoGenerateEnabled = true)
    {
        var plan = new MaintenancePlan
        {
            TenantId = harness.TenantProvider.TenantId!.Value,
            UnitId = harness.UnitId,
            Name = "Coverage plan",
            IntervalDays = intervalDays,
            FirstDueDate = firstDueDate ?? FutureFirstDue,
            AssetCategoryId = harness.CategoryId,
            IsActive = isActive,
            AutoGenerateEnabled = autoGenerateEnabled,
        };
        harness.Db.MaintenancePlans.Add(plan);
        await harness.Db.SaveChangesAsync();
        return plan;
    }

    private static async Task<Asset> AddAssetAsync(
        BulkCreateAssetsHarness harness,
        string tag,
        AssetStatus status = AssetStatus.Active,
        DateTimeOffset? scheduledDeletionAt = null,
        Guid? unitId = null,
        Guid? categoryId = null,
        bool requiresMaintenance = false)
    {
        var asset = new Asset
        {
            TenantId = harness.TenantProvider.TenantId!.Value,
            UnitId = unitId ?? harness.UnitId,
            CategoryId = categoryId ?? harness.CategoryId,
            FamilyId = harness.FamilyId,
            Name = $"Split {tag}",
            Tag = tag,
            Status = status,
            ScheduledDeletionAt = scheduledDeletionAt,
            RequiresMaintenance = requiresMaintenance,
        };
        harness.Db.Assets.Add(asset);
        await harness.Db.SaveChangesAsync();
        return asset;
    }

    private static async Task<WorkOrder> AddWorkOrderAsync(
        BulkCreateAssetsHarness harness,
        Guid? maintenancePlanId,
        Guid assetId,
        WorkOrderStatus status,
        DateOnly scheduledDate,
        DateTimeOffset? completedDate = null)
    {
        var workOrder = new WorkOrder
        {
            TenantId = harness.TenantProvider.TenantId!.Value,
            AssetId = assetId,
            MaintenancePlanId = maintenancePlanId,
            Status = status,
            ScheduledDate = scheduledDate,
            CompletedDate = completedDate,
            SourcePlanName = maintenancePlanId is null ? null : "Coverage plan",
        };
        harness.Db.WorkOrders.Add(workOrder);
        await harness.Db.SaveChangesAsync();
        return workOrder;
    }
}
