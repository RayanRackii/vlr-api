using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Pmoc.Dtos;

public sealed record MaintenancePlanCoverageResponse(
    Guid PlanId,
    DateOnly AsOfDate,
    int IntervalDays,
    DateOnly FirstDueDate,
    bool IsActive,
    bool AutoGenerateEnabled,
    bool WouldBeConsideredByGenerator,
    MaintenancePlanCoverageSummary Summary,
    IReadOnlyList<MaintenancePlanCoverageAssetItem> Assets)
{
    public int EligibleAssetCount => Summary.EligibleAssets;
}

public sealed record MaintenancePlanCoverageSummary(
    int EligibleAssets,
    int AssetsWithPmocHistory,
    int AssetsNeverExecuted,
    int AssetsExecuted,
    int AssetsNotDue,
    int AssetsDueToday,
    int AssetsOverdue,
    int AssetsNeedingAttention,
    int AssetsWithOpenWorkOrder);

public sealed record MaintenancePlanCoverageAssetItem(
    Guid AssetId,
    string Name,
    string Tag,
    MaintenancePlanLastMaintenance? LastMaintenance,
    DateOnly NextDueDate,
    PmocHistoryStatus HistoryStatus,
    PmocDueStatus DueStatus,
    bool NeedsAttention,
    MaintenancePlanOpenWorkOrder? OpenWorkOrder);

public sealed record MaintenancePlanLastMaintenance(
    Guid WorkOrderId,
    DateOnly ScheduledDate,
    DateTimeOffset? CompletedDate);

public sealed record MaintenancePlanOpenWorkOrder(
    Guid WorkOrderId,
    WorkOrderStatus Status,
    DateOnly ScheduledDate);
