using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// One concrete occurrence of a resource allocation backing an order item's requirement,
/// realized through exactly one <see cref="ReservationItem"/>. Plain data holder — mutation
/// and transition rules live in the allocation service (later work).
/// </summary>
public class CatalogOrderItemResourceAllocation : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid RequirementId { get; set; }

    /// <summary>Unique: one allocation per reservation item.</summary>
    public required Guid ReservationItemId { get; set; }

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

    /// <summary>The actually assigned resource. Phase 0: always equals the requirement's RentalAssetId.</summary>
    public required Guid RentalAssetId { get; set; }

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

    public ReservationItem ReservationItem { get; set; } = null!;
}
