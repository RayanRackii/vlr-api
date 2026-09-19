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

    [Fact]
    public async Task Eligible_active_matching_asset_is_included()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedMonthlyPlanAsync(harness);
        var asset = await AddAssetAsync(harness, tag: "AC-01");

        var coverage = await GetCoverageAsync(harness, plan.Id);

        var row = Assert.Single(coverage.Assets);
        Assert.Equal(asset.Id, row.AssetId);
        Assert.Equal("AC-01", row.Tag);
        Assert.Equal(PmocOperationalStatus.NeverExecuted, row.OperationalStatus);
        Assert.Equal(1, coverage.Summary.EligibleAssets);
        Assert.Equal(coverage.Summary.EligibleAssets, coverage.EligibleAssetCount);
        Assert.Equal(
            coverage.Summary.EligibleAssets,
            coverage.Summary.AssetsNeverExecuted
            + coverage.Summary.AssetsOverdue
            + coverage.Summary.AssetsOnTrack);
    }

    [Fact]
    public async Task Inactive_asset_is_excluded()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedMonthlyPlanAsync(harness);
        await AddAssetAsync(harness, tag: "OFF", status: AssetStatus.Inactive);

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.Empty(coverage.Assets);
        Assert.Equal(0, coverage.Summary.EligibleAssets);
    }

    [Fact]
    public async Task Maintenance_status_asset_is_excluded()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedMonthlyPlanAsync(harness);
        await AddAssetAsync(harness, tag: "MNT", status: AssetStatus.Maintenance);

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.Empty(coverage.Assets);
    }

    [Fact]
    public async Task Scheduled_deletion_asset_is_excluded()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedMonthlyPlanAsync(harness);
        await AddAssetAsync(harness, tag: "DEL", scheduledDeletionAt: DateTimeOffset.UtcNow);

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.Empty(coverage.Assets);
    }

    [Fact]
    public async Task Wrong_unit_asset_is_excluded()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedMonthlyPlanAsync(harness);
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
        var plan = await SeedMonthlyPlanAsync(harness);
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
        var plan = await SeedMonthlyPlanAsync(harness);
        var withoutFlag = await AddAssetAsync(harness, tag: "NO-FLAG", requiresMaintenance: false);
        var withFlag = await AddAssetAsync(harness, tag: "FLAG", requiresMaintenance: true);

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.Equal(2, coverage.Summary.EligibleAssets);
        Assert.Contains(coverage.Assets, row => row.AssetId == withoutFlag.Id);
        Assert.Contains(coverage.Assets, row => row.AssetId == withFlag.Id);
    }

    [Fact]
    public async Task Never_executed_when_no_completed_work_order()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedMonthlyPlanAsync(harness);
        var asset = await AddAssetAsync(harness, tag: "NEVER");
        await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.Pending,
            scheduledDate: AsOf);

        var coverage = await GetCoverageAsync(harness, plan.Id);

        var row = Assert.Single(coverage.Assets);
        Assert.Equal(PmocOperationalStatus.NeverExecuted, row.OperationalStatus);
        Assert.Null(row.LastMaintenance);
        Assert.NotNull(row.OpenWorkOrder);
        Assert.Equal(1, coverage.Summary.AssetsNeverExecuted);
        Assert.Equal(0, coverage.Summary.AssetsWithPmocHistory);
    }

    [Fact]
    public async Task On_track_when_completed_scheduled_date_covers_current_window()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedMonthlyPlanAsync(harness);
        var asset = await AddAssetAsync(harness, tag: "OK");
        var completed = await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.Completed,
            scheduledDate: new DateOnly(2026, 9, 1),
            completedDate: DateTimeOffset.Parse("2026-09-02T12:00:00Z"));

        var coverage = await GetCoverageAsync(harness, plan.Id);

        var row = Assert.Single(coverage.Assets);
        Assert.Equal(PmocOperationalStatus.OnTrack, row.OperationalStatus);
        Assert.Equal(completed.Id, row.LastMaintenance!.WorkOrderId);
        Assert.Equal(new DateOnly(2026, 9, 1), coverage.LastDueDate);
        Assert.Equal(new DateOnly(2026, 10, 1), coverage.NextDueDate);
        Assert.Equal(coverage.NextDueDate, row.NextDueDate);
        Assert.Equal(1, coverage.Summary.AssetsOnTrack);
    }

    [Fact]
    public async Task Overdue_when_completed_history_does_not_cover_current_window()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedMonthlyPlanAsync(harness);
        var asset = await AddAssetAsync(harness, tag: "LATE");
        await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.Completed,
            scheduledDate: new DateOnly(2026, 8, 1),
            completedDate: DateTimeOffset.Parse("2026-08-02T12:00:00Z"));

        var coverage = await GetCoverageAsync(harness, plan.Id);

        var row = Assert.Single(coverage.Assets);
        Assert.Equal(PmocOperationalStatus.Overdue, row.OperationalStatus);
        Assert.Equal(1, coverage.Summary.AssetsOverdue);
        Assert.Equal(1, coverage.Summary.AssetsWithPmocHistory);
    }

    [Fact]
    public async Task Canceled_work_order_is_ignored()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedMonthlyPlanAsync(harness);
        var asset = await AddAssetAsync(harness, tag: "CAN");
        await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.Canceled,
            scheduledDate: new DateOnly(2026, 9, 1),
            completedDate: DateTimeOffset.Parse("2026-09-02T12:00:00Z"));

        var coverage = await GetCoverageAsync(harness, plan.Id);

        var row = Assert.Single(coverage.Assets);
        Assert.Equal(PmocOperationalStatus.NeverExecuted, row.OperationalStatus);
        Assert.Null(row.LastMaintenance);
        Assert.Null(row.OpenWorkOrder);
    }

    [Fact]
    public async Task Manual_work_order_without_plan_is_ignored()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedMonthlyPlanAsync(harness);
        var asset = await AddAssetAsync(harness, tag: "MAN");
        await AddWorkOrderAsync(
            harness,
            maintenancePlanId: null,
            asset.Id,
            WorkOrderStatus.Completed,
            scheduledDate: new DateOnly(2026, 9, 1),
            completedDate: DateTimeOffset.Parse("2026-09-02T12:00:00Z"));

        var coverage = await GetCoverageAsync(harness, plan.Id);

        var row = Assert.Single(coverage.Assets);
        Assert.Equal(PmocOperationalStatus.NeverExecuted, row.OperationalStatus);
        Assert.Null(row.LastMaintenance);
    }

    [Fact]
    public async Task Last_maintenance_uses_latest_completed_date_then_scheduled_date_then_id()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedMonthlyPlanAsync(harness);
        var asset = await AddAssetAsync(harness, tag: "HIST");
        var older = await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.Completed,
            scheduledDate: new DateOnly(2026, 9, 15),
            completedDate: DateTimeOffset.Parse("2026-09-10T12:00:00Z"));
        var newer = await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.Completed,
            scheduledDate: new DateOnly(2026, 9, 1),
            completedDate: DateTimeOffset.Parse("2026-09-18T12:00:00Z"));
        Assert.NotEqual(older.Id, newer.Id);

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.Equal(newer.Id, Assert.Single(coverage.Assets).LastMaintenance!.WorkOrderId);
    }

    [Fact]
    public async Task Last_maintenance_id_desc_breaks_equal_completed_and_scheduled()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedMonthlyPlanAsync(harness);
        var asset = await AddAssetAsync(harness, tag: "TIE");
        var completed = DateTimeOffset.Parse("2026-09-10T12:00:00Z");
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
        var plan = await SeedMonthlyPlanAsync(harness);
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
    public async Task Open_work_order_among_pending_uses_earliest_scheduled_date_then_id()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedMonthlyPlanAsync(harness);
        var asset = await AddAssetAsync(harness, tag: "PEND");
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
            scheduledDate: new DateOnly(2026, 9, 2));
        _ = later;

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.Equal(earlier.Id, Assert.Single(coverage.Assets).OpenWorkOrder!.WorkOrderId);
    }

    [Fact]
    public async Task Summary_counts_only_eligible_assets_and_each_has_one_status()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedMonthlyPlanAsync(harness);
        var never = await AddAssetAsync(harness, tag: "Z-NEVER");
        var overdue = await AddAssetAsync(harness, tag: "A-LATE");
        var onTrack = await AddAssetAsync(harness, tag: "M-OK");
        await AddWorkOrderAsync(
            harness,
            plan.Id,
            overdue.Id,
            WorkOrderStatus.Completed,
            scheduledDate: new DateOnly(2026, 8, 1),
            completedDate: DateTimeOffset.Parse("2026-08-02T12:00:00Z"));
        await AddWorkOrderAsync(
            harness,
            plan.Id,
            onTrack.Id,
            WorkOrderStatus.Completed,
            scheduledDate: new DateOnly(2026, 9, 1),
            completedDate: DateTimeOffset.Parse("2026-09-02T12:00:00Z"));
        _ = never;

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.Equal(3, coverage.Summary.EligibleAssets);
        Assert.Equal(1, coverage.Summary.AssetsNeverExecuted);
        Assert.Equal(1, coverage.Summary.AssetsOverdue);
        Assert.Equal(1, coverage.Summary.AssetsOnTrack);
        Assert.Equal(2, coverage.Summary.AssetsWithPmocHistory);
        Assert.Equal(
            coverage.Summary.EligibleAssets,
            coverage.Summary.AssetsNeverExecuted
            + coverage.Summary.AssetsOverdue
            + coverage.Summary.AssetsOnTrack);
        Assert.Equal(
            [
                PmocOperationalStatus.Overdue,
                PmocOperationalStatus.NeverExecuted,
                PmocOperationalStatus.OnTrack,
            ],
            coverage.Assets.Select(row => row.OperationalStatus).ToArray());
        Assert.Equal(overdue.Id, coverage.Assets[0].AssetId);
        Assert.Equal(never.Id, coverage.Assets[1].AssetId);
        Assert.Equal(onTrack.Id, coverage.Assets[2].AssetId);
        Assert.All(coverage.Assets, row => Assert.Equal(coverage.AsOfDate, AsOf));
    }

    [Fact]
    public async Task Status_filter_limits_rows_but_not_summary()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedMonthlyPlanAsync(harness);
        await AddAssetAsync(harness, tag: "NEVER");
        var overdue = await AddAssetAsync(harness, tag: "LATE");
        await AddWorkOrderAsync(
            harness,
            plan.Id,
            overdue.Id,
            WorkOrderStatus.Completed,
            scheduledDate: new DateOnly(2026, 8, 1),
            completedDate: DateTimeOffset.Parse("2026-08-02T12:00:00Z"));

        var coverage = await GetCoverageAsync(
            harness,
            plan.Id,
            [PmocOperationalStatus.Overdue]);

        Assert.Equal(2, coverage.Summary.EligibleAssets);
        Assert.Equal(1, coverage.Summary.AssetsOverdue);
        Assert.Equal(1, coverage.Summary.AssetsNeverExecuted);
        var row = Assert.Single(coverage.Assets);
        Assert.Equal(PmocOperationalStatus.Overdue, row.OperationalStatus);
        Assert.Equal(overdue.Id, row.AssetId);
    }

    [Fact]
    public async Task Generator_visibility_true_when_active_auto_and_due_even_with_zero_assets()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(
            harness,
            MaintenanceFrequency.Daily,
            isActive: true,
            autoGenerateEnabled: true);

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.True(coverage.IsDueToday);
        Assert.True(coverage.WouldBeConsideredByGenerator);
        Assert.Equal(0, coverage.Summary.EligibleAssets);
    }

    [Fact]
    public async Task Generator_visibility_false_when_auto_off()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(
            harness,
            MaintenanceFrequency.Daily,
            isActive: true,
            autoGenerateEnabled: false);

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.False(coverage.WouldBeConsideredByGenerator);
    }

    [Fact]
    public async Task Generator_visibility_false_when_plan_inactive()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(
            harness,
            MaintenanceFrequency.Daily,
            isActive: false,
            autoGenerateEnabled: true);
        await AddAssetAsync(harness, tag: "STILL");

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.False(coverage.WouldBeConsideredByGenerator);
        Assert.Equal(1, coverage.Summary.EligibleAssets);
        Assert.False(coverage.IsActive);
    }

    [Fact]
    public async Task Generator_visibility_false_when_not_due_today()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedMonthlyPlanAsync(harness, autoGenerateEnabled: true, isActive: true);

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.False(coverage.IsDueToday);
        Assert.False(coverage.WouldBeConsideredByGenerator);
        Assert.Equal(AsOf, coverage.AsOfDate);
        Assert.Equal(PmocDueCalendar.LastDueOnOrBefore(MaintenanceFrequency.Monthly, AsOf), coverage.LastDueDate);
        Assert.Equal(PmocDueCalendar.NextDueOnOrAfter(MaintenanceFrequency.Monthly, AsOf), coverage.NextDueDate);
    }

    [Fact]
    public async Task Missing_plan_returns_null()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();

        var coverage = await CreateService(harness)
            .GetCoverageAsync(Guid.NewGuid(), statuses: null, CancellationToken.None);

        Assert.Null(coverage);
    }

    [Fact]
    public async Task Foreign_tenant_plan_returns_null()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedMonthlyPlanAsync(harness);
        var originalTenant = harness.TenantProvider.TenantId;
        harness.TenantProvider.TenantId = Guid.NewGuid();

        var coverage = await CreateService(harness)
            .GetCoverageAsync(plan.Id, statuses: null, CancellationToken.None);

        Assert.Null(coverage);
        harness.TenantProvider.TenantId = originalTenant;
    }

    [Fact]
    public async Task Many_assets_are_joined_in_memory_without_per_asset_round_trips()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedMonthlyPlanAsync(harness);
        for (var i = 0; i < 20; i++)
        {
            var asset = await AddAssetAsync(harness, tag: $"AC-{i:D2}");
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
    }

    [Fact]
    public async Task Calendar_fields_share_one_as_of_date()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedMonthlyPlanAsync(harness);

        var coverage = await GetCoverageAsync(harness, plan.Id);

        Assert.Equal(AsOf, coverage.AsOfDate);
        Assert.Equal(coverage.IsDueToday, PmocDueCalendar.IsDueToday(plan.Frequency, coverage.AsOfDate));
        Assert.Equal(
            plan.IsActive && plan.AutoGenerateEnabled && coverage.IsDueToday,
            coverage.WouldBeConsideredByGenerator);
    }

    private static MaintenancePlanCoverageService CreateService(BulkCreateAssetsHarness harness) =>
        new(
            harness.Db,
            harness.TenantProvider,
            new TestTimeProvider(BrazilTimeZone.AtLocal(AsOf, new TimeOnly(12, 0))));

    private static async Task<Platform.Api.Modules.Pmoc.Dtos.MaintenancePlanCoverageResponse> GetCoverageAsync(
        BulkCreateAssetsHarness harness,
        Guid planId,
        IReadOnlyCollection<PmocOperationalStatus>? statuses = null)
    {
        var coverage = await CreateService(harness)
            .GetCoverageAsync(planId, statuses, CancellationToken.None);
        Assert.NotNull(coverage);
        return coverage!;
    }

    private static async Task<MaintenancePlan> SeedMonthlyPlanAsync(
        BulkCreateAssetsHarness harness,
        bool isActive = true,
        bool autoGenerateEnabled = true) =>
        await SeedPlanAsync(
            harness,
            MaintenanceFrequency.Monthly,
            isActive,
            autoGenerateEnabled);

    private static async Task<MaintenancePlan> SeedPlanAsync(
        BulkCreateAssetsHarness harness,
        MaintenanceFrequency frequency,
        bool isActive,
        bool autoGenerateEnabled)
    {
        var plan = new MaintenancePlan
        {
            TenantId = harness.TenantProvider.TenantId!.Value,
            UnitId = harness.UnitId,
            Name = "Coverage plan",
            Frequency = frequency,
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
