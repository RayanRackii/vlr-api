using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Dynamic price window for a rental asset by day of week and time range.
/// </summary>
public class RentalPricing : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid RentalAssetId { get; set; }

    public required DayOfWeek DayOfWeek { get; set; }

    public required TimeOnly StartTime { get; set; }

    public required TimeOnly EndTime { get; set; }

    public required decimal PricePerHour { get; set; }

    public required bool RequiresDeposit { get; set; }

    public required decimal DepositPercentage { get; set; }

    public RentalAsset RentalAsset { get; set; } = null!;
}
