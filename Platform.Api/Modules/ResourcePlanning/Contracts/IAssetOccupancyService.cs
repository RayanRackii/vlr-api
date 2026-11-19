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
    string TimeZoneId);

/// <summary>
/// A work window with its stored occupied window (work plus prep and cleanup).
/// <c>HoursViolation</c> is null when the hours policy allows the window.
/// </summary>
public sealed record AllocationWindow(
    DateTimeOffset WorkStart,
    DateTimeOffset WorkEnd,
    DateTimeOffset OccupiedStart,
    DateTimeOffset OccupiedEnd,
    string? HoursViolation);

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
/// The planning port: who holds an asset's calendar. This backend is planning-only (the
/// <c>AssetAllocation</c> table is the occupancy); the rentals bridge for rental-backed assets
/// arrives in a later slice behind the same port. Every member runs inside the caller's
/// transaction and none of them saves, the convention of the rentals occupancy port. Callers
/// take <see cref="LockAssetsAsync"/> first and keep the lock until they commit.
/// </summary>
public interface IAssetOccupancyService
{
    /// <summary><c>SELECT ... FOR UPDATE</c> on the asset rows (ascending id) of the tenant.</summary>
    Task LockAssetsAsync(Guid tenantId, IEnumerable<Guid> assetIds, CancellationToken cancellationToken);

    /// <summary>
    /// Eligibility: the asset exists in the tenant (404 otherwise), is active, not scheduled for
    /// deletion and plannable, is not rental-backed and is not closed all week
    /// (409 <c>planning.asset_not_plannable</c> otherwise).
    /// </summary>
    Task<PlannableAsset> ResolvePlannableAssetAsync(
        Guid tenantId,
        Guid assetId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Builds the occupied window and applies the maximum duration (400) and the hours policy
    /// (reported in <see cref="AllocationWindow.HoursViolation"/>, not thrown, so a preview can show
    /// it). <c>Unavailable</c> blocks are not subject to working hours: they exist to close an
    /// asset on days it would otherwise be open.
    /// </summary>
    AllocationWindow ValidateWindow(
        PlannableAsset asset,
        AssetAllocationKind kind,
        DateTimeOffset workStart,
        DateTimeOffset workEnd,
        int? prepMinutes,
        int? cleanupMinutes);

    /// <summary>
    /// Overlap check over the blocking allocations of the asset (half-open windows, capacity,
    /// never-started manual allocations expired after their end). <paramref name="excludeAllocationId"/>
    /// leaves the allocation being edited out.
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
    /// blocking allocation (a started one that is overdue still blocks). Null capacity data falls
    /// back to one unit.
    /// </summary>
    Task<AvailabilityResult> CheckStartAsync(
        Guid tenantId,
        AssetAllocation allocation,
        CancellationToken cancellationToken);

    /// <summary>Adds the allocation to the context (not saved) and returns it.</summary>
    AssetAllocation Occupy(Guid tenantId, PlannableAsset asset, NewAllocation request);

    /// <summary>
    /// Frees the asset when an allocation is released or cancelled. A no-op for planning-only
    /// assets (the status change frees them); the rentals bridge releases its mirror here.
    /// </summary>
    Task ReleaseAsync(AssetAllocation allocation, CancellationToken cancellationToken);
}
