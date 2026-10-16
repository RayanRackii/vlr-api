using Platform.Api.Modules.Rentals.Dtos;

namespace Platform.Api.Modules.Rentals.Services;

public interface IBookingPolicyService
{
    Task<BookingPolicyResponseDto> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Replaces every policy field. Omitted or null properties clear the stored value.
    /// </summary>
    Task<BookingPolicyResponseDto> UpsertAsync(
        UpsertBookingPolicyRequestDto request,
        CancellationToken cancellationToken);
}
