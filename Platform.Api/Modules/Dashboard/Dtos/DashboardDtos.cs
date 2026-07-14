namespace Platform.Api.Modules.Dashboard.Dtos;

public sealed record AssetMetricsDto(
    int Total,
    int Active,
    int InMaintenance,
    int Inactive);

public sealed record WorkOrderMetricsDto(
    int TotalThisMonth,
    int Pending,
    int InProgress,
    int Completed,
    int Canceled);

public sealed record DashboardMetricsDto(
    AssetMetricsDto Assets,
    WorkOrderMetricsDto WorkOrders);
