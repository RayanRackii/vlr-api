using System.Text.Json.Serialization;
using Platform.Api.Serialization;

namespace Platform.Api.Modules.Rentals.Dtos;

public sealed record BookingPolicyResponseDto(
    string? CancellationPolicyText,
    int? MaxActiveReservationsPerCustomerPerDay,
    int? MaxAdvanceBookingDays,
    int? CancelCutoffHours = null,
    int? MinimumBookingLeadMinutes = null,
    int? DepositPercent = null);

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

    /// <summary>Omitted keeps the lead time; explicit null clears it (no minimum).</summary>
    [JsonConverter(typeof(JsonOptionalInt32Converter))]
    public JsonOptional<int> MinimumBookingLeadMinutes { get; init; }

    /// <summary>Omitted keeps the percent; explicit null clears it (no deposit percent).</summary>
    [JsonConverter(typeof(JsonOptionalInt32Converter))]
    public JsonOptional<int> DepositPercent { get; init; }
}

public sealed record CategoryBookingPolicyResponseDto(
    Guid CategoryId,
    string CategoryName,
    int? MaxAdvanceBookingDays,
    int? CancelCutoffHours,
    int? MinimumBookingLeadMinutes = null,
    int? DepositPercent = null);

public sealed class UpsertCategoryBookingPolicyRequestDto
{
    public UpsertCategoryBookingPolicyRequestDto()
    {
    }

    public UpsertCategoryBookingPolicyRequestDto(
        int? maxAdvanceBookingDays,
        int? cancelCutoffHours,
        JsonOptional<int> minimumBookingLeadMinutes = default,
        JsonOptional<int> depositPercent = default)
    {
        MaxAdvanceBookingDays = maxAdvanceBookingDays;
        CancelCutoffHours = cancelCutoffHours;
        MinimumBookingLeadMinutes = minimumBookingLeadMinutes;
        DepositPercent = depositPercent;
    }

    public int? MaxAdvanceBookingDays { get; init; }

    public int? CancelCutoffHours { get; init; }

    /// <summary>Omitted keeps the stored lead; explicit null clears it.</summary>
    [JsonConverter(typeof(JsonOptionalInt32Converter))]
    public JsonOptional<int> MinimumBookingLeadMinutes { get; init; }

    /// <summary>Omitted keeps the stored percent; explicit null clears it.</summary>
    [JsonConverter(typeof(JsonOptionalInt32Converter))]
    public JsonOptional<int> DepositPercent { get; init; }
}
