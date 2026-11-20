using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.ResourcePlanning.Services;

/// <summary>
/// The blocking rule and the lifecycle of an <see cref="AssetAllocation"/> in one place, used by
/// the writer (conflict check, transitions) and the readers (agenda). Pure and clock-free: every
/// member that depends on time takes <c>now</c>.
/// </summary>
/// <remarks>
/// <c>Scheduled</c> blocks its occupied window. <c>Running</c> and <c>Cleaning</c> block from the
/// occupied start until released, with no implicit end. <c>Released</c> and <c>Cancelled</c> never
/// block. A manual allocation that was never started stops blocking once its occupied window has
/// ended (owner decision, ADR 0010); allocations of other sources keep blocking. An allocation
/// bridged to a rental-backed asset (it has a <c>ReservationItemId</c>) never expires: its mirror
/// <c>InternalUse</c> reservation keeps blocking in rentals until it is released or cancelled (an
/// overdue unreleased internal-use reservation blocks, with no job to close it), so expiring only
/// the planning side would let the two calendars disagree.
/// </remarks>
public static class AssetAllocationRules
{
    /// <summary>Longest work window of one allocation on a planning-only asset (30 days).</summary>
    public const int MaxDurationMinutes = 43_200;

    public static bool IsExpired(AssetAllocation allocation, DateTimeOffset now) =>
        allocation.Status == AllocationStatus.Scheduled
        && allocation.Source == AssetAllocationSource.Manual
        && allocation.ReservationItemId is null
        && allocation.OccupiedEnd <= now;

    public static bool IsBlocking(AssetAllocation allocation, DateTimeOffset now) =>
        allocation.Status switch
        {
            AllocationStatus.Scheduled => !IsExpired(allocation, now),
            AllocationStatus.Running or AllocationStatus.Cleaning => true,
            _ => false,
        };

    /// <summary>
    /// Started and still held after its time: past the planned work end while Running, past the
    /// occupied end (cleanup included) while Cleaning.
    /// </summary>
    public static bool IsOverdue(AssetAllocation allocation, DateTimeOffset now) =>
        allocation.Status switch
        {
            AllocationStatus.Running => allocation.PlannedWorkEnd <= now,
            AllocationStatus.Cleaning => allocation.OccupiedEnd <= now,
            _ => false,
        };

    /// <summary>
    /// Start of the interval the allocation blocks. A started allocation blocks from the earlier of
    /// its occupied start and the moment it actually started, so an early start holds the asset
    /// from that moment.
    /// </summary>
    public static DateTimeOffset BlockingStart(AssetAllocation allocation) =>
        allocation.Status is AllocationStatus.Running or AllocationStatus.Cleaning
        && allocation.ActualStartAt is { } started
        && started < allocation.OccupiedStart
            ? started
            : allocation.OccupiedStart;

    /// <summary>End of the interval the allocation blocks: open-ended once started.</summary>
    public static DateTimeOffset BlockingEnd(AssetAllocation allocation) =>
        allocation.Status is AllocationStatus.Running or AllocationStatus.Cleaning
            ? DateTimeOffset.MaxValue
            : allocation.OccupiedEnd;

    public static void Start(AssetAllocation allocation, DateTimeOffset now)
    {
        if (allocation.Status != AllocationStatus.Scheduled)
        {
            throw PlanningException.InvalidTransition(
                $"Cannot start: allocation is {allocation.Status}, expected Scheduled.");
        }

        // An expired allocation has stopped blocking, so another allocation may now hold the
        // window. Starting it would make it block again from its old start and overlap that one.
        if (IsExpired(allocation, now))
        {
            throw PlanningException.InvalidTransition(
                "Cannot start: the allocation expired because it was never started before its end.");
        }

        allocation.Status = AllocationStatus.Running;
        allocation.ActualStartAt = now;
    }

    public static void WorkFinished(AssetAllocation allocation, DateTimeOffset now)
    {
        if (allocation.Status != AllocationStatus.Running)
        {
            throw PlanningException.InvalidTransition(
                $"Cannot mark work finished: allocation is {allocation.Status}, expected Running.");
        }

        allocation.Status = AllocationStatus.Cleaning;
        allocation.ActualWorkFinishedAt = now;
    }

    public static void Release(AssetAllocation allocation, DateTimeOffset now)
    {
        if (allocation.Status != AllocationStatus.Cleaning)
        {
            throw PlanningException.InvalidTransition(
                $"Cannot release: allocation is {allocation.Status}, expected Cleaning.");
        }

        allocation.Status = AllocationStatus.Released;
        allocation.ActualReleaseAt = now;
    }

    public static void Cancel(AssetAllocation allocation, DateTimeOffset now, string? reason)
    {
        if (allocation.Status != AllocationStatus.Scheduled)
        {
            throw PlanningException.InvalidTransition(
                $"Cannot cancel: allocation is {allocation.Status}; only a Scheduled allocation can be cancelled.");
        }

        allocation.Status = AllocationStatus.Cancelled;
        allocation.ActualReleaseAt = now;
        allocation.CancelReason = reason;
    }
}
