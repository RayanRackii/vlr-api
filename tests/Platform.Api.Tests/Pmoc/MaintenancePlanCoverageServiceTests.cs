using Platform.Api.Modules.Pmoc.Dtos;
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
    public async Task A_never_executed_future_first_due_is_not_due()
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
        Assert.Equal(FutureFirstDue, row.EffectiveNextDueDate);
        Assert.Null(row.LastMaintenance);
        AssertSummaryInvariants(coverage);
        Assert.False(coverage.WouldBeConsideredByGenerator);
    }

    [Fact]
    public async Task B_never_executed_today_first_due_is_due_today()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: AsOf);
        await AddAssetAsync(harness, tag: "TODAY");

        var coverage = await GetCoverageAsync(harness, plan.Id);

        var row = Assert.Single(coverage.Assets);
        Assert.Equal(PmocHistoryStatus.NeverExecuted, row.HistoryStatus);
        Assert.Equal(PmocDueStatus.DueToday, row.DueStatus);
        Assert.True(row.NeedsAttention);
        Assert.Equal(AsOf, row.EffectiveNextDueDate);
        Assert.Null(row.LastMaintenance);
        AssertSummaryInvariants(coverage);
        Assert.Equal(1, coverage.Summary.AssetsDueToday);
        Assert.True(coverage.WouldBeConsideredByGenerator);
    }

    [Fact]
    public async Task C_never_executed_past_first_due_is_overdue()
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
        Assert.Equal(new DateOnly(2026, 9, 1), row.EffectiveNextDueDate);
        AssertSummaryInvariants(coverage);
        Assert.True(coverage.WouldBeConsideredByGenerator);
    }

    [Fact]
    public async Task D_executed_future_calculated_due_is_not_due()
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
        Assert.False(row.NeedsAttention);
        Assert.Equal(new DateOnly(2026, 10, 2), row.EffectiveNextDueDate);
        Assert.Equal(completed.Id, row.LastMaintenance!.WorkOrderId);
        Assert.Equal(completed.CompletedDate, row.LastMaintenance.CompletedDate);
        AssertSummaryInvariants(coverage);
        Assert.False(coverage.WouldBeConsideredByGenerator);
    }

    [Fact]
    public async Task E_executed_today_calculated_due_is_due_today()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, intervalDays: 30, firstDueDate: FutureFirstDue);
        var asset = await AddAssetAsync(harness, tag: "DUE");
        await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.Completed,
            scheduledDate: new DateOnly(2026, 8, 19),
            completedDate: BrazilTimeZone.AtLocal(new DateOnly(2026, 8, 20), new TimeOnly(12, 0)));

        var coverage = await GetCoverageAsync(harness, plan.Id);

        var row = Assert.Single(coverage.Assets);
        Assert.Equal(PmocHistoryStatus.Executed, row.HistoryStatus);
        Assert.Equal(PmocDueStatus.DueToday, row.DueStatus);
        Assert.True(row.NeedsAttention);
        Assert.Equal(AsOf, row.EffectiveNextDueDate);
        AssertSummaryInvariants(coverage);
        Assert.True(coverage.WouldBeConsideredByGenerator);
    }

    [Fact]
    public async Task F_executed_past_calculated_due_is_overdue()
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
        Assert.Equal(new DateOnly(2026, 9, 1), row.EffectiveNextDueDate);
        Assert.Equal(1, coverage.Summary.AssetsOverdue);
        AssertSummaryInvariants(coverage);
        Assert.True(coverage.WouldBeConsideredByGenerator);
    }

    [Fact]
    public async Task G_never_executed_does_not_imply_attention()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: FutureFirstDue);
        await AddAssetAsync(harness, tag: "G");

        var coverage = await GetCoverageAsync(harness, plan.Id);

        var row = Assert.Single(coverage.Assets);
        Assert.Equal(PmocHistoryStatus.NeverExecuted, row.HistoryStatus);
        Assert.False(row.NeedsAttention);
        Assert.Equal(0, coverage.Summary.AssetsNeedingAttention);
    }

    [Fact]
    public async Task H_due_today_implies_attention()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: AsOf);
        await AddAssetAsync(harness, tag: "H");

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.True(Assert.Single(coverage.Assets).NeedsAttention);
        Assert.Equal(1, coverage.Summary.AssetsNeedingAttention);
    }

    [Fact]
    public async Task I_overdue_implies_attention()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: new DateOnly(2026, 8, 1));
        await AddAssetAsync(harness, tag: "I");

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.True(Assert.Single(coverage.Assets).NeedsAttention);
        Assert.Equal(1, coverage.Summary.AssetsNeedingAttention);
    }

    [Fact]
    public async Task JKL_summary_history_due_and_attention_invariants()
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
        Assert.Equal(0, coverage.Summary.AssetsDueToday);
        Assert.Equal(1, coverage.Summary.AssetsNeedingAttention);
        Assert.Equal(overdue.Id, coverage.Assets[0].AssetId);
        AssertSummaryInvariants(coverage);
        Assert.True(coverage.WouldBeConsideredByGenerator);
    }

    [Fact]
    public async Task M_last_maintenance_uses_latest_completed_date_then_scheduled_date_then_id()
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
        Assert.Equal(new DateOnly(2026, 10, 18), row.EffectiveNextDueDate);
    }

    [Fact]
    public async Task M_last_maintenance_id_desc_breaks_equal_completed_and_scheduled()
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
    public async Task N_pending_work_order_does_not_reset_clock()
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
        Assert.Equal(FutureFirstDue, row.EffectiveNextDueDate);
        Assert.Null(row.LastMaintenance);
        Assert.NotNull(row.OpenWorkOrder);
        Assert.Equal(1, coverage.Summary.AssetsNeverExecuted);
        Assert.Equal(0, coverage.Summary.AssetsExecuted);
        AssertSummaryInvariants(coverage);
    }

    [Fact]
    public async Task O_in_progress_work_order_does_not_reset_clock()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: FutureFirstDue);
        var asset = await AddAssetAsync(harness, tag: "WIP");
        var open = await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.InProgress,
            scheduledDate: AsOf);

        var coverage = await GetCoverageAsync(harness, plan.Id);

        var row = Assert.Single(coverage.Assets);
        Assert.Equal(PmocHistoryStatus.NeverExecuted, row.HistoryStatus);
        Assert.Equal(PmocDueStatus.NotDue, row.DueStatus);
        Assert.Equal(FutureFirstDue, row.EffectiveNextDueDate);
        Assert.Null(row.LastMaintenance);
        Assert.Equal(open.Id, row.OpenWorkOrder!.WorkOrderId);
        Assert.Equal(WorkOrderStatus.InProgress, row.OpenWorkOrder.Status);
    }

    [Fact]
    public async Task P_canceled_work_order_does_not_reset_clock()
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
        Assert.Equal(FutureFirstDue, row.EffectiveNextDueDate);
        Assert.Null(row.LastMaintenance);
        Assert.Null(row.OpenWorkOrder);
    }

    [Fact]
    public async Task Q_manual_work_order_without_plan_does_not_reset_clock()
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
        Assert.Equal(FutureFirstDue, row.EffectiveNextDueDate);
    }

    [Fact]
    public async Task R_open_work_order_prefers_in_progress_over_pending_regardless_of_date()
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
    public async Task S_open_work_order_uses_earliest_scheduled_date_then_id()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: FutureFirstDue);
        var asset = await AddAssetAsync(harness, tag: "TIE-OPEN");
        var later = await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.Pending,
            scheduledDate: new DateOnly(2026, 10, 1));
        var earlier = await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.Pending,
            scheduledDate: new DateOnly(2026, 9, 20));
        _ = later;

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.Equal(earlier.Id, Assert.Single(coverage.Assets).OpenWorkOrder!.WorkOrderId);

        var sameDateAsset = await AddAssetAsync(harness, tag: "TIE-ID");
        var first = await AddWorkOrderAsync(
            harness,
            plan.Id,
            sameDateAsset.Id,
            WorkOrderStatus.Pending,
            scheduledDate: new DateOnly(2026, 9, 21));
        var second = await AddWorkOrderAsync(
            harness,
            plan.Id,
            sameDateAsset.Id,
            WorkOrderStatus.Pending,
            scheduledDate: new DateOnly(2026, 9, 21));
        var expectedId = first.Id.CompareTo(second.Id) < 0 ? first.Id : second.Id;

        coverage = await GetCoverageAsync(harness, plan.Id);
        var tieRow = Assert.Single(coverage.Assets, row => row.AssetId == sameDateAsset.Id);
        Assert.Equal(expectedId, tieRow.OpenWorkOrder!.WorkOrderId);
    }

    [Fact]
    public async Task T_open_work_order_does_not_change_due_status()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, intervalDays: 30);
        var asset = await AddAssetAsync(harness, tag: "OPEN-LATE");
        await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.Completed,
            scheduledDate: new DateOnly(2026, 8, 1),
            completedDate: BrazilTimeZone.AtLocal(new DateOnly(2026, 8, 2), new TimeOnly(12, 0)));
        var pending = await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.Pending,
            scheduledDate: AsOf);

        var coverage = await GetCoverageAsync(harness, plan.Id);

        var row = Assert.Single(coverage.Assets);
        Assert.Equal(PmocDueStatus.Overdue, row.DueStatus);
        Assert.True(row.NeedsAttention);
        Assert.Equal(pending.Id, row.OpenWorkOrder!.WorkOrderId);
        Assert.Equal(PmocHistoryStatus.Executed, row.HistoryStatus);
        Assert.Equal(new DateOnly(2026, 9, 1), row.EffectiveNextDueDate);
    }

    [Fact]
    public async Task U_inactive_plan_still_returns_coverage_rows()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(
            harness,
            isActive: false,
            firstDueDate: new DateOnly(2026, 9, 1));
        var asset = await AddAssetAsync(harness, tag: "INACT");

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.False(coverage.IsActive);
        Assert.Equal(asset.Id, Assert.Single(coverage.Assets).AssetId);
        Assert.Equal(PmocDueStatus.Overdue, coverage.Assets[0].DueStatus);
        Assert.False(coverage.WouldBeConsideredByGenerator);
    }

    [Fact]
    public async Task V_auto_generate_false_still_returns_coverage_rows()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(
            harness,
            autoGenerateEnabled: false,
            firstDueDate: new DateOnly(2026, 9, 1));
        var asset = await AddAssetAsync(harness, tag: "NOAUTO");

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.False(coverage.AutoGenerateEnabled);
        Assert.Equal(asset.Id, Assert.Single(coverage.Assets).AssetId);
        Assert.Equal(PmocDueStatus.Overdue, coverage.Assets[0].DueStatus);
        Assert.False(coverage.WouldBeConsideredByGenerator);
    }

    [Fact]
    public async Task W_generator_visibility_false_when_plan_inactive()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(
            harness,
            isActive: false,
            autoGenerateEnabled: true,
            firstDueDate: AsOf);
        await AddAssetAsync(harness, tag: "W");

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.Equal(PmocDueStatus.DueToday, Assert.Single(coverage.Assets).DueStatus);
        Assert.False(coverage.WouldBeConsideredByGenerator);
    }

    [Fact]
    public async Task X_generator_visibility_false_when_auto_off()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(
            harness,
            isActive: true,
            autoGenerateEnabled: false,
            firstDueDate: AsOf);
        await AddAssetAsync(harness, tag: "X");

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.Equal(PmocDueStatus.DueToday, Assert.Single(coverage.Assets).DueStatus);
        Assert.False(coverage.WouldBeConsideredByGenerator);
    }

    [Fact]
    public async Task Y_generator_visibility_false_when_all_assets_not_due()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(
            harness,
            isActive: true,
            autoGenerateEnabled: true,
            firstDueDate: FutureFirstDue);
        await AddAssetAsync(harness, tag: "Y");

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.Equal(PmocDueStatus.NotDue, Assert.Single(coverage.Assets).DueStatus);
        Assert.False(coverage.WouldBeConsideredByGenerator);
    }

    [Fact]
    public async Task Z_generator_visibility_true_when_at_least_one_due_today()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(
            harness,
            isActive: true,
            autoGenerateEnabled: true,
            firstDueDate: AsOf);
        await AddAssetAsync(harness, tag: "Z");

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.Equal(PmocDueStatus.DueToday, Assert.Single(coverage.Assets).DueStatus);
        Assert.True(coverage.WouldBeConsideredByGenerator);
    }

    [Fact]
    public async Task AA_generator_visibility_true_when_at_least_one_overdue()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(
            harness,
            isActive: true,
            autoGenerateEnabled: true,
            firstDueDate: new DateOnly(2026, 9, 1));
        await AddAssetAsync(harness, tag: "AA");

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.Equal(PmocDueStatus.Overdue, Assert.Single(coverage.Assets).DueStatus);
        Assert.True(coverage.WouldBeConsideredByGenerator);
    }

    [Fact]
    public async Task Generator_visibility_false_when_no_eligible_assets()
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
        AssertSummaryInvariants(coverage);
    }

    [Fact]
    public async Task AB_inactive_asset_is_excluded()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness);
        await AddAssetAsync(harness, tag: "OFF", status: AssetStatus.Inactive);

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.Empty(coverage.Assets);
        Assert.Equal(0, coverage.Summary.EligibleAssets);
    }

    [Fact]
    public async Task AB_maintenance_status_asset_is_excluded()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness);
        await AddAssetAsync(harness, tag: "MNT", status: AssetStatus.Maintenance);

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.Empty(coverage.Assets);
    }

    [Fact]
    public async Task AB_scheduled_deletion_asset_is_excluded()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness);
        await AddAssetAsync(harness, tag: "DEL", scheduledDeletionAt: DateTimeOffset.UtcNow);

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.Empty(coverage.Assets);
    }

    [Fact]
    public async Task AB_wrong_unit_asset_is_excluded()
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
    public async Task AB_wrong_category_asset_is_excluded()
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
    public async Task AC_requires_maintenance_false_does_not_exclude()
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
    public async Task AD_foreign_tenant_plan_returns_null()
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
    public async Task Missing_plan_returns_null()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();

        var coverage = await CreateService(harness)
            .GetCoverageAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(coverage);
    }

    [Fact]
    public async Task Sort_uses_due_then_tag_name_then_asset_id()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: FutureFirstDue);
        var first = await AddAssetAsync(harness, tag: "dup");
        var second = await AddAssetAsync(harness, tag: "DUP");
        var expectedOrder = first.Id.CompareTo(second.Id) < 0
            ? new[] { first.Id, second.Id }
            : new[] { second.Id, first.Id };

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.Equal(expectedOrder, coverage.Assets.Select(row => row.AssetId));
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
        AssertSummaryInvariants(coverage);
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

    private static void AssertSummaryInvariants(MaintenancePlanCoverageResponse coverage)
    {
        var summary = coverage.Summary;
        Assert.Equal(summary.EligibleAssets, coverage.EligibleAssetCount);
        Assert.Equal(summary.EligibleAssets, coverage.Assets.Count);
        Assert.Equal(
            summary.EligibleAssets,
            summary.AssetsNeverExecuted + summary.AssetsExecuted);
        Assert.Equal(
            summary.EligibleAssets,
            summary.AssetsNotDue + summary.AssetsDueToday + summary.AssetsOverdue);
        Assert.Equal(
            summary.AssetsNeedingAttention,
            summary.AssetsDueToday + summary.AssetsOverdue);
        Assert.All(coverage.Assets, row => Assert.Equal(coverage.AsOfDate, AsOf));
        Assert.All(
            coverage.Assets,
            row => Assert.Equal(
                row.DueStatus is PmocDueStatus.DueToday or PmocDueStatus.Overdue,
                row.NeedsAttention));
    }

    private static MaintenancePlanCoverageService CreateService(BulkCreateAssetsHarness harness) =>
        new(
            harness.Db,
            harness.TenantProvider,
            new TestTimeProvider(BrazilTimeZone.AtLocal(AsOf, new TimeOnly(12, 0))));

    private static async Task<MaintenancePlanCoverageResponse> GetCoverageAsync(
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
