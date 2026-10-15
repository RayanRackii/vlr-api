namespace Platform.Api.Modules.Rentals.Dtos;

public sealed record BookingPolicyResponseDto(
    string? CancellationPolicyText,
    int? MaxActiveReservationsPerCustomerPerDay);

/// <summary>
/// Full replacement of the tenant default booking policy.
/// Send both properties on every request. A property that is omitted or null
/// clears the stored value. The server does not merge with the previous row.
/// </summary>
public sealed record UpsertBookingPolicyRequestDto
{
    public string? CancellationPolicyText { get; init; }

    public int? MaxActiveReservationsPerCustomerPerDay { get; init; }
}
