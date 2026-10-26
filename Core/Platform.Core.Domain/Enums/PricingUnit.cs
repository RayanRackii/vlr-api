using System.Text.Json.Serialization;

namespace Platform.Core.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter<PricingUnit>))]
public enum PricingUnit
{
    Hour = 0,
    Slot = 1,
    Day = 2,
    Fixed = 3,
}
