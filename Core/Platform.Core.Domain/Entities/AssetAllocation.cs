using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// The planning record: a window of use (or unavailability) on one <see cref="Asset"/>. Slice API-2
/// introduces the table only; the services that write it arrive with the allocation slices.
/// Plain data holder; transition and blocking rules live in the planning services.
/// </summary>
public class AssetAllocation : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid AssetId { get; set; }

    /// <summary>Snapshot of the asset's unit at creation time, for filtering.</summary>
    public required Guid UnitId { get; set; }

    public AssetAllocationKind Kind { get; set; } = AssetAllocationKind.Use;

    public required string Title { get; set; }

    public string? Notes { get; set; }

    public int Quantity { get; set; } = 1;

    public required DateTimeOffset PlannedWorkStart { get; set; }

    public required DateTimeOffset PlannedWorkEnd { get; set; }

    /// <summary>Work window plus prep and cleanup, stored so readers never pad.</summary>
    public required DateTimeOffset OccupiedStart { get; set; }

    public required DateTimeOffset OccupiedEnd { get; set; }

    /// <summary>Snapshot of the tenant effective timezone id used for planning.</summary>
    public required string TimeZoneId { get; set; }

    public AllocationStatus Status { get; set; } = AllocationStatus.Scheduled;

    public DateTimeOffset? ActualStartAt { get; set; }

    public DateTimeOffset? ActualWorkFinishedAt { get; set; }

    public DateTimeOffset? ActualReleaseAt { get; set; }

    public AssetAllocationSource Source { get; set; } = AssetAllocationSource.Manual;

    public Guid? CatalogOrderItemId { get; set; }

    public Guid? WorkOrderId { get; set; }

    /// <summary>Display snapshot ("Pedido 123", "OS 45") so the agenda needs no cross-module join.</summary>
    public string? ReferenceLabel { get; set; }

    /// <summary>Set only on rental-backed assets (mirror InternalUse reservation item).</summary>
    public Guid? ReservationItemId { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public Guid? ActorId { get; set; }

    public string? CancelReason { get; set; }

    /// <summary>Database-enforced optimistic concurrency token, re-rolled on every mutation.</summary>
    public byte[] RowVersion { get; set; } = Guid.NewGuid().ToByteArray();
}
