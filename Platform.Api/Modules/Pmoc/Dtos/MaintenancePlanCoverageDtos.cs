using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Pmoc.Dtos;

public sealed record MaintenancePlanCoverageResponse(
    Guid PlanId,
    DateOnly AsOfDate,
    MaintenanceFrequency Frequency,
    DateOnly LastDueDate,
    DateOnly NextDueDate,
    bool IsActive,
    bool AutoGenerateEnabled,
    bool IsDueToday,
    bool WouldBeConsideredByGenerator,
    MaintenancePlanCoverageSummary Summary,
    IReadOnlyList<MaintenancePlanCoverageAssetItem> Assets);

public sealed record MaintenancePlanCoverageSummary(
    int EligibleAssets,
    int AssetsWithPmocHistory,
    int AssetsNeverExecuted,
    int AssetsOverdue,
    int AssetsOnTrack,
    int AssetsWithOpenWorkOrder);

public sealed record MaintenancePlanCoverageAssetItem(
    Guid AssetId,
    string Name,
    string Tag,
    MaintenancePlanLastMaintenance? LastMaintenance,
    DateOnly NextDueDate,
    PmocOperationalStatus OperationalStatus,
    MaintenancePlanOpenWorkOrder? OpenWorkOrder);

public sealed record MaintenancePlanLastMaintenance(
    Guid WorkOrderId,
    DateOnly ScheduledDate,
    DateTimeOffset? CompletedDate);

public sealed record MaintenancePlanOpenWorkOrder(
    Guid WorkOrderId,
    WorkOrderStatus Status,
    DateOnly ScheduledDate);
