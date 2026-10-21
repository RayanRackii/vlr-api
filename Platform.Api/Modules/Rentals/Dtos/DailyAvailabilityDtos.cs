using System.Text.Json.Serialization;

namespace Platform.Api.Modules.Rentals.Dtos;

[JsonConverter(typeof(JsonStringEnumConverter<AvailabilityWindowState>))]
public enum AvailabilityWindowState { Available, Taken, Blocked, Past, OutsideHorizon, Unpriced }

public sealed record DailyAvailabilityResponseDto(
    DateOnly Date, string TimeZone, DateTimeOffset ServerNow, DateTimeOffset QuoteExpiresAt,
    string Currency, IReadOnlyList<ResourceAvailabilityDto> Resources);

public sealed record ResourceAvailabilityDto(
    Guid RentalAssetId, Guid AssetId, Guid UnitId, string Name, string Type,
    int TotalQuantity, bool RequiresDeposit, bool QueueEnabled,
    int? EffectiveMaxAdvanceBookingDays, IReadOnlyList<AvailabilityWindowDto> Windows);

public sealed record AvailabilityWindowDto(
    Guid? SlotId, TimeOnly StartTime, TimeOnly EndTime, bool IsDerived,
    [property: JsonConverter(typeof(JsonStringEnumConverter<AvailabilityWindowState>))]
    AvailabilityWindowState State, string? ReasonCode, int AvailableQuantity, decimal? TotalAmount);
