namespace Platform.Api.Modules.ResourcePlanning.Dtos;

/// <summary>
/// A window of use (<c>Kind = Use</c>) or unavailability (<c>Kind = Unavailable</c>) on one
/// plannable asset. Times are instants; <c>WorkStart</c>/<c>WorkEnd</c> are the work window and the
/// buffers (prep, cleanup) are added around it to form the occupied window.
/// </summary>
public sealed record CreateAssetAllocationRequest
{
    public Guid AssetId { get; init; }

    /// <summary><c>Use</c> (default) or <c>Unavailable</c>.</summary>
    public string Kind { get; init; } = "Use";

    public string Title { get; init; } = string.Empty;

    public string? Notes { get; init; }

    public DateTimeOffset WorkStart { get; init; }

    public DateTimeOffset WorkEnd { get; init; }

    /// <summary>Null uses the asset's default prep minutes.</summary>
    public int? PrepMinutes { get; init; }

    /// <summary>Null uses the asset's default cleanup minutes.</summary>
    public int? CleanupMinutes { get; init; }
}

/// <summary>
/// Edits a <c>Scheduled</c> manual allocation. Null fields keep their value; an empty
/// <c>Notes</c> clears them. <c>ExpectedVersion</c> is the <c>Version</c> the caller read.
/// </summary>
public sealed record UpdateAssetAllocationRequest
{
    public string? ExpectedVersion { get; init; }

    public string? Title { get; init; }

    public string? Notes { get; init; }

    public DateTimeOffset? WorkStart { get; init; }

    public DateTimeOffset? WorkEnd { get; init; }

    public int? PrepMinutes { get; init; }

    public int? CleanupMinutes { get; init; }
}

/// <summary>
/// <c>Action</c> is <c>start</c>, <c>work-finished</c>, <c>release</c> or <c>cancel</c>.
/// <c>ExpectedVersion</c> is the <c>Version</c> the caller read.
/// </summary>
public sealed record AssetAllocationUsageRequest
{
    public string Action { get; init; } = string.Empty;

    public string? ExpectedVersion { get; init; }

    public string? Reason { get; init; }
}

/// <summary>One blocker found by the conflict check. <c>Source</c> is <c>allocation</c> or <c>reservation</c>.</summary>
public sealed record AllocationConflictDto(
    string Source,
    Guid? AllocationId,
    DateTimeOffset Start,
    DateTimeOffset End,
    string? Label);

/// <summary>
/// Result of <c>POST /api/planning/allocations/preview</c>. <c>HoursViolation</c> is
/// <c>start_outside_working_hours</c> or <c>outside_working_hours</c> when the hours policy rejects
/// the window; the window is feasible only when there is no violation and no conflict.
/// </summary>
public sealed record AllocationPreviewResponse(
    bool Feasible,
    DateTimeOffset OccupiedStart,
    DateTimeOffset OccupiedEnd,
    IReadOnlyList<AllocationConflictDto> Conflicts,
    string? HoursViolation);

/// <summary>
/// One allocation. <c>Version</c> is an opaque concurrency token to echo back as
/// <c>ExpectedVersion</c>. <c>IsBlocking</c> says whether it holds the asset now;
/// <c>IsExpired</c> marks a manual allocation never started whose end passed (no longer blocking);
/// <c>IsOverdue</c> marks one that was started and is still held past its planned end.
/// </summary>
public sealed record AssetAllocationResponse(
    Guid Id,
    Guid AssetId,
    string AssetName,
    Guid UnitId,
    string Kind,
    string Title,
    string? Notes,
    int Quantity,
    DateTimeOffset PlannedWorkStart,
    DateTimeOffset PlannedWorkEnd,
    DateTimeOffset OccupiedStart,
    DateTimeOffset OccupiedEnd,
    string TimeZoneId,
    string Status,
    DateTimeOffset? ActualStartAt,
    DateTimeOffset? ActualWorkFinishedAt,
    DateTimeOffset? ActualReleaseAt,
    string Source,
    string? ReferenceLabel,
    Guid? CreatedByUserId,
    string? CancelReason,
    bool IsBlocking,
    bool IsExpired,
    bool IsOverdue,
    string Version,
    DateTimeOffset CreatedAt);

/// <summary>An asset row of the agenda with its working hours, for shading non-working time.</summary>
public sealed record PlanningAgendaResourceDto(
    Guid AssetId,
    string Name,
    string Tag,
    Guid UnitId,
    string UnitName,
    int Capacity,
    string HoursPolicy,
    bool IsRentalBacked,
    string TimeZoneId,
    IReadOnlyList<AssetWorkingHoursWindowDto> WorkingHours);

/// <summary>An anonymous busy interval from another calendar (customer reservations). Empty until the rentals bridge.</summary>
public sealed record PlanningExternalBusyDto(Guid AssetId, DateTimeOffset Start, DateTimeOffset End);

/// <summary>
/// <c>GET /api/planning/agenda</c>: plannable assets, the allocations overlapping the range by
/// their stored occupied window (cancelled ones are left out), and external busy intervals.
/// </summary>
public sealed record PlanningAgendaResponse(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<PlanningAgendaResourceDto> Resources,
    IReadOnlyList<AssetAllocationResponse> Items,
    IReadOnlyList<PlanningExternalBusyDto> ExternalBusy);
