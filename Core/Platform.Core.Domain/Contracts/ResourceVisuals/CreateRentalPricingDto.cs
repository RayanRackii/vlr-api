namespace Platform.Core.Domain.Contracts.ResourceVisuals;

/// <summary>Neutral pricing shape used by resource visual defaults and Rental pricing inputs.</summary>
public sealed record CreateRentalPricingDto
{
    public required DayOfWeek DayOfWeek { get; init; }
    public required TimeOnly StartTime { get; init; }
    public required TimeOnly EndTime { get; init; }
    public required decimal PricePerHour { get; init; }
    public bool RequiresDeposit { get; init; }
    public decimal DepositPercentage { get; init; }
}
