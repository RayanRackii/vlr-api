using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Dashboard.Dtos;

public sealed record CustomerActivityMetricsDto(
    int LoggedInToday,
    int LoggedInLast7Days,
    int LoggedInLast30Days,
    int TotalCustomers);

public sealed record AssetFamilyCountDto(
    string Key,
    string Label,
    int Count);

public sealed record AssetMetricsDto(
    int Total,
    int Active,
    int InMaintenance,
    int Inactive,
    IReadOnlyList<AssetFamilyCountDto> ByFamily);

public sealed record WorkOrderMetricsDto(
    int TotalThisMonth,
    int Pending,
    int InProgress,
    int Completed,
    int Canceled);

public sealed record PmocMetricsDto(
    int ActivePlans,
    int WorkOrdersFromPlansThisMonth,
    int? ElectricalTotal);

public sealed record MaintenanceMetricsDto(
    int AssetsInMaintenance,
    int OpenWorkOrders);

public sealed record RentalsMetricsDto(
    int ReservationsTodayPendingDeposit,
    int ReservationsTodayConfirmed,
    int ReservationsTodayCanceled,
    int ReservationsTodayCompleted,
    double ConfirmationRateLast7Days,
    int SlotsAvailableToday,
    int SlotsBookedToday,
    decimal ReservedRevenueThisMonth,
    int RentableSpaces,
    int RentableGoods);

public sealed record DashboardMetricsDto(
    CustomerActivityMetricsDto CustomerActivity,
    AssetMetricsDto? Assets,
    WorkOrderMetricsDto? WorkOrders,
    PmocMetricsDto? Pmoc,
    MaintenanceMetricsDto? Maintenance,
    RentalsMetricsDto? Rentals);
