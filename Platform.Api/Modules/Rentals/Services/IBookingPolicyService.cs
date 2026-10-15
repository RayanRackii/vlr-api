using Platform.Api.Modules.Rentals.Dtos;

namespace Platform.Api.Modules.Rentals.Services;

public interface IBookingPolicyService
{
    Task<BookingPolicyResponseDto> GetAsync(CancellationToken cancellationToken);

    Task<BookingPolicyResponseDto> UpsertAsync(
        UpsertBookingPolicyRequestDto request,
        CancellationToken cancellationToken);
}
