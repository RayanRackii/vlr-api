using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// One dated occupancy cell on a Rentable. Schema: rentals.
/// </summary>
public class Slot : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid RentalAssetId { get; set; }

    public required DateOnly Date { get; set; }

    public required TimeOnly StartTime { get; set; }

    public required TimeOnly EndTime { get; set; }

    public required Guid OccupancyKindId { get; set; }

    public string? Label { get; set; }

    public required SlotStatus Status { get; set; }

    public Guid? ReservationId { get; set; }

    public Guid? SourceTemplateId { get; set; }

    public RentalAsset RentalAsset { get; set; } = null!;

    public OccupancyKind OccupancyKind { get; set; } = null!;

    public Reservation? Reservation { get; set; }

    public ScheduleTemplate? SourceTemplate { get; set; }

    public void MarkBooked(Guid reservationId)
    {
        Status = SlotStatus.Booked;
        ReservationId = reservationId;
        MarkAsUpdated();
    }

    public void MarkCancelled()
    {
        Status = SlotStatus.Cancelled;
        ReservationId = null;
        MarkAsUpdated();
    }

    public void MarkAvailable()
    {
        Status = SlotStatus.Available;
        ReservationId = null;
        MarkAsUpdated();
    }
}
