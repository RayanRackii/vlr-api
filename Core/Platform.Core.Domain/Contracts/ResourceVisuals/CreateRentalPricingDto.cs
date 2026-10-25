using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Contracts.ResourceVisuals;

/// <summary>Neutral pricing shape used by resource visual defaults and Rental pricing inputs.</summary>
public sealed record CreateRentalPricingDto
{
    public required DayOfWeek DayOfWeek { get; init; }
    public required TimeOnly StartTime { get; init; }
    public required TimeOnly EndTime { get; init; }
    public required decimal PricePerHour { get; init; }
    [EnumDataType(typeof(PricingUnit))]
    [JsonConverter(typeof(JsonStringEnumConverter<PricingUnit>))]
    public PricingUnit PricingUnit { get; init; } = PricingUnit.Hour;
    public bool RequiresDeposit { get; init; }
    public decimal DepositPercentage { get; init; }
}
