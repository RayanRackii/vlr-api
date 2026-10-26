using System.Text.Json.Serialization;
using Platform.Api.Serialization;

namespace Platform.Api.Modules.Rentals.Dtos;

public sealed record BookingPolicyResponseDto(
    string? CancellationPolicyText,
    int? MaxActiveReservationsPerCustomerPerDay,
    int? MaxAdvanceBookingDays,
    int? CancelCutoffHours = null);

/// <summary>
/// Replacement of the tenant default booking policy.
/// <c>cancellationPolicyText</c> and <c>maxActiveReservationsPerCustomerPerDay</c>
/// are full replace: omitted or null clears the stored value.
/// <c>maxAdvanceBookingDays</c> is presence-aware: omitted keeps the stored horizon,
/// explicit null clears it (unlimited).
/// </summary>
public sealed record UpsertBookingPolicyRequestDto
{
    public string? CancellationPolicyText { get; init; }

    public int? MaxActiveReservationsPerCustomerPerDay { get; init; }

    [JsonConverter(typeof(JsonOptionalInt32Converter))]
    public JsonOptional<int> MaxAdvanceBookingDays { get; init; }

    /// <summary>Omitted keeps the cutoff; explicit null clears it.</summary>
    [JsonConverter(typeof(JsonOptionalInt32Converter))]
    public JsonOptional<int> CancelCutoffHours { get; init; }
}

public sealed record CategoryBookingPolicyResponseDto(
    Guid CategoryId,
    string CategoryName,
    int? MaxAdvanceBookingDays,
    int? CancelCutoffHours);

public sealed record UpsertCategoryBookingPolicyRequestDto(
    int? MaxAdvanceBookingDays,
    int? CancelCutoffHours);
