using Platform.Api.Modules.ResourcePlanning.Dtos;
using Platform.Api.Modules.ResourcePlanning.Services;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.ResourcePlanning.Contracts;

/// <summary>An asset eligible for planning, with the profile facts the occupancy rules need.</summary>
public sealed record PlannableAsset(
    Guid AssetId,
    Guid UnitId,
    string Name,
    int Capacity,
    AssetHoursPolicy HoursPolicy,
    IReadOnlyList<WorkingHoursWindow> WorkingHours,
    int DefaultPrepMinutes,
    int DefaultCleanupMinutes,
    TimeZoneInfo Zone,
    string TimeZoneId,
    Guid? RentalAssetId = null)
{
    /// <summary>
    /// True when the asset has an active rental configuration and rentals is active: the rental
    /// schedule is the calendar authority, hours and window rules come from rentals, and every
    /// allocation is mirrored as an <c>InternalUse</c> reservation.
    /// </summary>
    public bool IsRentalBacked => RentalAssetId is not null;
}

/// <summary>
/// A work window with its stored occupied window (work plus prep and cleanup).
/// <c>HoursViolation</c> is null when the hours policy allows the window. On a rental-backed asset
/// <c>RentalWindowError</c> carries the reason the rental schedule rejects the window (not an
/// offerable booking window, spans several days) and is null otherwise.
/// </summary>
public sealed record AllocationWindow(
    DateTimeOffset WorkStart,
    DateTimeOffset WorkEnd,
    DateTimeOffset OccupiedStart,
    DateTimeOffset OccupiedEnd,
    string? HoursViolation,
    string? RentalWindowError = null);

/// <summary>The blockers found for a window; empty means the asset is free.</summary>
public sealed record AvailabilityResult(IReadOnlyList<AllocationConflictDto> Conflicts)
{
    public bool IsFree => Conflicts.Count == 0;
}

/// <summary>What a new allocation record holds (the caller supplies who and what).</summary>
public sealed record NewAllocation(
    AssetAllocationKind Kind,
    string Title,
    string? Notes,
    AllocationWindow Window,
    Guid? CreatedByUserId);

/// <summary>
/// The planning port: who holds an asset's calendar, and the only writer of the occupancy stores.
/// Two backends sit behind it. A planning-only asset is occupied by its <c>AssetAllocation</c>
/// rows alone. A rental-backed asset (active <c>RentalAsset</c> and rentals active) keeps the rental
/// schedule as the calendar authority: every allocation is also an <c>InternalUse</c> reservation
/// written through the rentals contract (<c>IResourceOccupancyService</c>), the link is stored in
/// <c>AssetAllocation.ReservationItemId</c>, and window rules and conflicts are the rentals ones
/// (customer reservations block an allocation and the reverse). Every member runs inside the
/// caller's transaction and none of them saves, the convention of the rentals occupancy port.
/// Callers take <see cref="LockAssetsAsync"/> first and keep the lock until they commit.
/// Lock order: tenant-modules advisory lock, order or work-order row, asset rows ascending, then
/// rental-asset rows ascending (taken here, inside <see cref="LockAssetsAsync"/>).
/// </summary>
public interface IAssetOccupancyService
{
    /// <summary>
    /// <c>SELECT ... FOR UPDATE</c> on the asset rows (ascending id) of the tenant, then, when
    /// rentals is active, on the rental-asset rows of those assets (ascending id). Asset rows are
    /// always taken before rental-asset rows.
    /// </summary>
    Task LockAssetsAsync(Guid tenantId, IEnumerable<Guid> assetIds, CancellationToken cancellationToken);

    /// <summary>
    /// Eligibility: the asset exists in the tenant (404 otherwise), is active, not scheduled for
    /// deletion and plannable, and is not closed all week (409 <c>planning.asset_not_plannable</c>
    /// otherwise). A rental-backed asset is eligible too: it resolves with
    /// <see cref="PlannableAsset.RentalAssetId"/> set, no working hours (rentals owns them) and
    /// capacity one.
    /// </summary>
    Task<PlannableAsset> ResolvePlannableAssetAsync(
        Guid tenantId,
        Guid assetId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Builds the occupied window and applies the maximum duration (400) and the hours policy
    /// (reported in <see cref="AllocationWindow.HoursViolation"/>, not thrown, so a preview can show
    /// it). <c>Unavailable</c> blocks are not subject to working hours: they exist to close an
    /// asset on days it would otherwise be open. The rental-backed rules need the database, see
    /// <see cref="ValidateWindowAsync"/>.
    /// </summary>
    AllocationWindow ValidateWindow(
        PlannableAsset asset,
        AssetAllocationKind kind,
        DateTimeOffset workStart,
        DateTimeOffset workEnd,
        int? prepMinutes,
        int? cleanupMinutes);

    /// <summary>
    /// <see cref="ValidateWindow"/> plus, for a rental-backed asset, the rental schedule rules (the
    /// occupied window must be an offerable booking window within one day), reported in
    /// <see cref="AllocationWindow.RentalWindowError"/>.
    /// </summary>
    Task<AllocationWindow> ValidateWindowAsync(
        Guid tenantId,
        PlannableAsset asset,
        AssetAllocationKind kind,
        DateTimeOffset workStart,
        DateTimeOffset workEnd,
        int? prepMinutes,
        int? cleanupMinutes,
        CancellationToken cancellationToken);

    /// <summary>
    /// Overlap check (half-open windows, capacity, never-started manual allocations expired after
    /// their end). On a rental-backed asset the blockers are the blocking reservations of the rental
    /// asset (customer bookings and the mirrors of other allocations) plus allocations that predate
    /// the bridge and have no reservation. <paramref name="excludeAllocationId"/> leaves the
    /// allocation being edited (and its mirror) out.
    /// </summary>
    Task<AvailabilityResult> CheckAvailabilityAsync(
        Guid tenantId,
        PlannableAsset asset,
        AssetAllocationKind kind,
        AllocationWindow window,
        Guid? excludeAllocationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Re-checks occupancy when a Scheduled allocation starts. A started allocation blocks from the
    /// earlier of its occupied start and now, so the check covers that interval against every other
    /// blocking allocation (a started one that is overdue still blocks). A bridged allocation is
    /// checked over its reservation window, which an early start does not move. Null capacity data
    /// falls back to one unit.
    /// </summary>
    Task<AvailabilityResult> CheckStartAsync(
        Guid tenantId,
        AssetAllocation allocation,
        CancellationToken cancellationToken);

    /// <summary>
    /// Adds the allocation to the context (not saved) and returns it. On a rental-backed asset it
    /// also adds the <c>InternalUse</c> reservation and stores <c>ReservationItemId</c>; a race on
    /// the reservation window surfaces as <c>planning.conflict</c>.
    /// </summary>
    Task<AssetAllocation> OccupyAsync(
        Guid tenantId,
        PlannableAsset asset,
        NewAllocation request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves the occupancy of an edited allocation to <paramref name="window"/>: the mirror
    /// reservation of a bridged allocation follows (the caller assigns the allocation fields). A
    /// no-op for an allocation without a reservation.
    /// </summary>
    Task RescheduleAsync(
        Guid tenantId,
        AssetAllocation allocation,
        AllocationWindow window,
        CancellationToken cancellationToken);

    /// <summary>
    /// Frees the asset when an allocation is released or cancelled. A no-op for planning-only
    /// allocations (the status change frees them). A bridged allocation frees its mirror
    /// reservation: cancelled before it started frees the whole window, released after it started
    /// closes the window at release plus cleanup (the rentals release semantics).
    /// </summary>
    Task ReleaseAsync(AssetAllocation allocation, CancellationToken cancellationToken);

    /// <summary>
    /// Busy time of the rental calendars of the given assets over <c>[from, to)</c> that is not an
    /// allocation: customer reservations, and internal-use reservations that no allocation mirrors
    /// (an order allocated through the catalog), anonymous (no customer, no label). The map goes
    /// from rental asset id to asset id. Empty when it is empty; otherwise at most two queries
    /// whatever the number of assets.
    /// </summary>
    Task<IReadOnlyList<PlanningExternalBusyDto>> ListExternalBusyAsync(
        Guid tenantId,
        IReadOnlyDictionary<Guid, Guid> assetIdByRentalAssetId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);
}
