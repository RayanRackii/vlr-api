using System.Text.Json.Serialization;

namespace Platform.Api.Modules.Rentals.Dtos;

public sealed record RentalsReadinessResponse(
    bool Ready,
    IReadOnlyList<RentalsReadinessItem> Items);

public sealed record RentalsReadinessItem(
    string Key,
    bool Done,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Count = null);
