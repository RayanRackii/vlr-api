using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Rental configuration for a global <see cref="Asset"/> (1:1). Schema: rentals.
/// </summary>
public class RentalAsset : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid AssetId { get; set; }

    public required RentalAssetType Type { get; set; }

    public required int TotalQuantity { get; set; }

    public required bool IsActive { get; set; }

    /// <summary>
    /// When true, a Customer booking waits for admin payment confirmation
    /// (<see cref="ReservationStatus.PendingDeposit"/>). When false, the
    /// reservation opens as <see cref="ReservationStatus.Confirmed"/>.
    /// </summary>
    public required bool RequiresDeposit { get; set; }

    /// <summary>Default SlotGrid; OpenHours derives bookable windows from open/close.</summary>
    public required SchedulePolicy SchedulePolicy { get; set; }

    public TimeOnly? OpenTime { get; set; }

    public TimeOnly? CloseTime { get; set; }

    /// <summary>
    /// Comma-separated allowed booking durations in minutes for OpenHours (e.g. "60,120,180").
    /// </summary>
    public string? AllowedDurationMinutes { get; set; }

    /// <summary>
    /// When true, B2C booking of this Location requires a valid Active queue ticket
    /// for the current daily opening session. Goods must stay false.
    /// </summary>
    public bool QueueEnabled { get; set; }

    /// <summary>
    /// Wall-clock opening T in America/Sao_Paulo. Required when <see cref="QueueEnabled"/> is true.
    /// </summary>
    public TimeOnly? QueueOpeningTime { get; set; }

    public Asset Asset { get; set; } = null!;

    private readonly List<RentalPricing> _pricings = [];

    public IReadOnlyCollection<RentalPricing> Pricings => _pricings.AsReadOnly();
}
