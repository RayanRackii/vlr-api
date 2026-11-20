using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// One concrete occurrence of a resource allocation backing an order item's requirement. The
/// occupancy itself is the linked <see cref="AssetAllocation"/> (<see cref="AssetAllocationId"/>);
/// on a rental-backed asset it is also realized through exactly one <see cref="ReservationItem"/>
/// (the mirror reservation). Plain data holder — mutation and transition rules live in the
/// allocation service.
/// </summary>
public class CatalogOrderItemResourceAllocation : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid RequirementId { get; set; }

    /// <summary>
    /// The planning record that holds the occupancy (slice API-5). Unique when set. Null only on a
    /// row that has not been linked yet; the Phase 0 backfill links every existing row.
    /// </summary>
    public Guid? AssetAllocationId { get; set; }

    /// <summary>
    /// Unique when set: one allocation per reservation item. Null when the asset is planning-only
    /// (not rental-backed): no reservation exists for it.
    /// </summary>
    public Guid? ReservationItemId { get; set; }

    public required int Sequence { get; set; }

    /// <summary>The <see cref="CatalogOrder.PlanningRevision"/> value at creation time.</summary>
    public required int PlanningRevision { get; set; }

    /// <summary>
    /// Database-enforced optimistic concurrency token (H3: PlanningRevision alone is an
    /// application-level check, not atomic with the write — two usage actions reading the same
    /// PlanningRevision could otherwise both commit). Re-rolled on every mutation alongside
    /// PlanningRevision; EF maps this with <c>IsConcurrencyToken()</c> so a stale write throws
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>.
    /// </summary>
    public byte[] RowVersion { get; set; } = Guid.NewGuid().ToByteArray();

    /// <summary>True for the active/current occurrence; false once superseded (Phase 1 reschedule).</summary>
    public bool IsCurrent { get; set; } = true;

    public required int AllocatedQuantity { get; set; }

    /// <summary>
    /// The rental configuration of the assigned asset. Set only when the asset is rental-backed;
    /// null for a planning-only asset (see <see cref="AssetAllocation"/> for the asset itself).
    /// </summary>
    public Guid? RentalAssetId { get; set; }

    public required DateTimeOffset PlannedWorkStart { get; set; }

    public required DateTimeOffset PlannedWorkEnd { get; set; }

    /// <summary>Snapshot of the tenant effective timezone id used for planning.</summary>
    public required string TimeZoneId { get; set; }

    public AllocationStatus Status { get; set; } = AllocationStatus.Scheduled;

    public DateTimeOffset? ActualStartAt { get; set; }

    public DateTimeOffset? ActualWorkFinishedAt { get; set; }

    public DateTimeOffset? ActualReleaseAt { get; set; }

    public Guid? ActorId { get; set; }

    public string? Reason { get; set; }

    public CatalogOrderItemResourceRequirement Requirement { get; set; } = null!;

    public ReservationItem? ReservationItem { get; set; }

    public AssetAllocation? AssetAllocation { get; set; }
}
