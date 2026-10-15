namespace Platform.Api.Modules.Rentals.Dtos;

public sealed record BookingPolicyResponseDto(
    string? CancellationPolicyText,
    int? MaxActiveReservationsPerCustomerPerDay);

public sealed record UpsertBookingPolicyRequestDto
{
    public string? CancellationPolicyText { get; init; }

    public int? MaxActiveReservationsPerCustomerPerDay { get; init; }
}
