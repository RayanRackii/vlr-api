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

/// <summary>
/// Inventory counts. Present only when the inventory module is active.
/// <see cref="InMaintenance"/> counts assets whose status is Maintenance.
/// It does not count RequiresMaintenance or open work orders.
/// </summary>
public sealed record AssetMetricsDto(
    int Total,
    int Active,
    int InMaintenance,
    int Inactive,
    IReadOnlyList<AssetFamilyCountDto> ByFamily);

/// <summary>
/// Work orders whose ScheduledDate falls in the current tenant civil month.
/// Every status is included, and the status counts sum to <see cref="TotalThisMonth"/>.
/// GET /api/work-orders is not filtered by month, so its length can be higher.
/// <see cref="OpenDue"/> is independent of the month: it counts Pending and InProgress
/// work orders with ScheduledDate on or before the tenant civil today, in any month.
/// </summary>
public sealed record WorkOrderMetricsDto(
    int TotalThisMonth,
    int Pending,
    int InProgress,
    int Completed,
    int Canceled,
    int OpenDue);

public sealed record PmocMetricsDto(
    int ActivePlans,
    int WorkOrdersFromPlansThisMonth,
    int? ElectricalTotal);

/// <summary>
/// <see cref="OpenWorkOrders"/> counts every Pending and InProgress work order regardless of
/// ScheduledDate (no date cap, unlike <c>WorkOrderMetricsDto.OpenDue</c>).
/// </summary>
public sealed record MaintenanceMetricsDto(
    int AssetsInMaintenance,
    int OpenWorkOrders);

/// <summary>
/// Rentals dashboard counts. Only reservations with Purpose CustomerBooking are
/// counted (InternalUse rows from catalog order allocation are excluded), matching the
/// Reservas page. The ReservationsToday* fields count reservations that overlap the
/// tenant civil day (EndDateTime after day start and StartDateTime before day end).
/// <see cref="ConfirmationRateLast7Days"/> is Confirmed / (Confirmed + Canceled) among
/// reservations last updated in the window. <see cref="ReservedRevenueThisMonth"/> sums
/// PendingDeposit and Confirmed reservations whose start falls in the current
/// tenant civil month.
/// </summary>
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
