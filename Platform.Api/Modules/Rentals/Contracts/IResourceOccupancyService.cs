using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Contracts;

/// <summary>
/// Shared occupancy primitives backed by the existing Rentals booking core
/// (<see cref="Platform.Api.Modules.Rentals.Services.RentalAssetLocks"/> and
/// <see cref="Platform.Api.Modules.Rentals.Services.ReservationService"/> conflict-check logic),
/// so Catalog (and, in a later phase, OS) can check and reserve physical resource occupancy for a
/// specific <c>RentalAsset</c> over a half-open interval without duplicating the conflict-check SQL
/// or the advisory-lock discipline.
///
/// Lock order contract (per the Order-Resource Planning spec): module lock -&gt; order lock -&gt;
/// resource locks, acquired in ascending RentalAsset id order. Callers must never acquire an
/// owning order/OS lock from inside a resource-locked callback. <see cref="LockResourcesAsync"/>
/// and <see cref="CreateInternalUseOccupancyAsync"/> assume the caller already holds whatever
/// module/order locks it needs and owns the ambient transaction; this service never opens its own
/// transaction.
/// </summary>
public interface IResourceOccupancyService
{
    /// <summary>
    /// Locks the given RentalAsset ids, in ascending id order, within the caller's current
    /// ambient transaction. The caller must already be inside a transaction when this is used to
    /// guard a subsequent write. A thin pass-through to
    /// <see cref="Platform.Api.Modules.Rentals.Services.RentalAssetLocks.LockByRentalAssetIdsAsync"/>.
    /// </summary>
    Task LockResourcesAsync(
        AppDbContext db,
        Guid tenantId,
        IEnumerable<Guid> rentalAssetIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Read-only availability check for one resource over <c>[occupiedStart, occupiedEnd)</c>,
    /// considering persisted occupancy (every blocking <c>CustomerBooking</c> and
    /// <c>InternalUse</c> reservation) plus an optional set of tentative intervals from the same
    /// in-flight batch (e.g. other lines of the same order approval), so a multi-line batch can be
    /// validated against itself before anything is committed. Does not lock or mutate anything.
    /// </summary>
    Task<ResourceAvailabilityResult> CheckAvailabilityAsync(
        AppDbContext db,
        Guid tenantId,
        Guid rentalAssetId,
        DateTimeOffset occupiedStart,
        DateTimeOffset occupiedEnd,
        IReadOnlyCollection<TentativeOccupancy> tentativeBatch,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates a single-item <c>InternalUse</c> <see cref="Platform.Core.Domain.Entities.Reservation"/>
    /// (plus its one <see cref="Platform.Core.Domain.Entities.ReservationItem"/>) for the given
    /// resource and already-buffered interval, inside the caller's existing transaction/DbContext.
    /// Does not open a transaction and does not call <c>SaveChangesAsync</c> — the caller (Catalog's
    /// approval flow) owns the save/commit boundary so the reservation, allocation rows and outbox
    /// entries commit atomically. Revalidates availability under the resource lock before adding the
    /// entity (defense in depth against races) and throws <see cref="ResourceUnavailableException"/>
    /// rather than silently racing. The caller must have already called
    /// <see cref="LockResourcesAsync"/> for <paramref name="rentalAssetId"/> (ascending order, together
    /// with every other resource in the batch) before calling this method.
    /// </summary>
    Task<Guid> CreateInternalUseOccupancyAsync(
        AppDbContext db,
        Guid tenantId,
        Guid unitId,
        Guid rentalAssetId,
        int quantity,
        DateTimeOffset occupiedStart,
        DateTimeOffset occupiedEnd,
        CancellationToken cancellationToken);

    /// <summary>
    /// Closes the owning <c>InternalUse</c> reservation of <paramref name="reservationItemId"/> so
    /// the freed time becomes bookable again by customer bookings and other allocations (and drops
    /// from the staff agenda, which reads the same <c>BlockingStatuses</c>). The effect depends on
    /// <paramref name="releaseKind"/>:
    /// <list type="bullet">
    /// <item><description><see cref="InternalUseReleaseKind.CancelBeforeStart"/>: the work never
    /// started, so the entire planned window — prep, work and cleanup buffers alike — is freed by
    /// marking the reservation <see cref="Platform.Core.Domain.Enums.ReservationStatus.Canceled"/>
    /// (a non-blocking terminal status).</description></item>
    /// <item><description><see cref="InternalUseReleaseKind.ReleaseAfterStart"/>: the work had
    /// started, so the occupied window is closed at <c>releaseTime + cleanupBuffer</c> and the
    /// reservation is marked <see cref="Platform.Core.Domain.Enums.ReservationStatus.Completed"/>.
    /// An early release shortens the window (freeing the unused tail); a release at/after the
    /// planned end leaves it unchanged (an overdue reservation still extends to the actual release
    /// time to preserve the overrun in history).</description></item>
    /// </list>
    /// <paramref name="plannedWorkEnd"/> is the allocation's planned work end, used to recover the
    /// cleanup buffer from the persisted window. Does not open a transaction or save; the caller
    /// commits alongside its own allocation-status update.
    /// </summary>
    Task ReleaseInternalUseOccupancyAsync(
        AppDbContext db,
        Guid tenantId,
        Guid reservationItemId,
        InternalUseReleaseKind releaseKind,
        DateTimeOffset plannedWorkEnd,
        CancellationToken cancellationToken);

    /// <summary>
    /// Loads the given RentalAsset (scoped to <paramref name="tenantId"/>) and reports whether it
    /// is currently eligible for production use (active, asset not under maintenance/scheduled for
    /// deletion), together with its display name. Returns <c>null</c> if the resource does not
    /// exist for this tenant.
    /// </summary>
    Task<EligibleResource?> ResolveEligibleResourceAsync(
        AppDbContext db,
        Guid tenantId,
        Guid rentalAssetId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Validates that <c>[occupiedStart, occupiedEnd)</c> is an existing offerable booking window
    /// for <paramref name="rentalAssetId"/> — never rounding (order-equipment-link-codex §3:
    /// "accepts only buffered intervals expressible by existing booking windows and allowed
    /// durations") — and resolves the tenant's effective booking time zone used to evaluate it.
    /// Returns a non-null <see cref="WorkWindowValidation.Error"/> when the interval is not an
    /// expressible booking window; otherwise <c>Error</c> is null and <c>Zone</c>/<c>ZoneId</c> are
    /// always populated.
    /// </summary>
    Task<WorkWindowValidation> ValidateWorkWindowAsync(
        AppDbContext db,
        Guid tenantId,
        Guid rentalAssetId,
        DateTimeOffset occupiedStart,
        DateTimeOffset occupiedEnd,
        CancellationToken cancellationToken);
}

/// <summary>
/// How an <c>InternalUse</c> occupancy is being closed, per the Order-Resource Planning release
/// semantics: whether the work ever started determines whether the whole window is freed or only
/// its unused tail.
/// </summary>
public enum InternalUseReleaseKind
{
    /// <summary>Work never started: free the entire planned window (prep + work + cleanup).</summary>
    CancelBeforeStart,

    /// <summary>Work had started: close the window at the release moment plus the cleanup buffer.</summary>
    ReleaseAfterStart,
}

/// <summary>A RentalAsset eligible (or not) for production/internal use, by id and display name.</summary>
public sealed record EligibleResource(Guid Id, string Name, bool IsEligible, string? IneligibilityReason);

/// <summary>
/// Result of <see cref="IResourceOccupancyService.ValidateWorkWindowAsync"/>: the tenant's
/// effective booking time zone (<see cref="Zone"/>/<see cref="ZoneId"/>) plus a non-null
/// <see cref="Error"/> when the interval is not an expressible booking window.
/// </summary>
public sealed record WorkWindowValidation(TimeZoneInfo Zone, string ZoneId, string? Error)
{
    public const string UnsupportedWorkWindowCode = "unsupported_work_window";
}

/// <summary>A tentative (not-yet-persisted) occupancy interval from the same in-flight batch.</summary>
public sealed record TentativeOccupancy(Guid RentalAssetId, DateTimeOffset Start, DateTimeOffset End);

/// <summary>
/// Result of an availability check. <see cref="IsAvailable"/> false means
/// <see cref="ConflictReservationId"/> and/or <see cref="ConflictSource"/> identify why — enough
/// detail for a caller to build a <c>409 resource_unavailable</c> response.
/// </summary>
public sealed record ResourceAvailabilityResult(
    bool IsAvailable,
    Guid? ConflictReservationId,
    ResourceOccupancyConflictSource? ConflictSource,
    string? Reason);

public enum ResourceOccupancyConflictSource
{
    PersistedReservation,
    TentativeBatch,
}
