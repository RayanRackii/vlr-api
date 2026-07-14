using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Line item within a reservation (e.g. 1 court + 10 vests).
/// </summary>
public class ReservationItem : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid ReservationId { get; set; }

    public required Guid RentalAssetId { get; set; }

    public required int Quantity { get; set; }

    public required decimal UnitPrice { get; set; }

    public required decimal SubTotal { get; set; }

    public Reservation Reservation { get; set; } = null!;

    public RentalAsset RentalAsset { get; set; } = null!;
}
