using Platform.Api.Modules.Rentals.Dtos;

namespace Platform.Api.Modules.Rentals.Services;

public interface IReservationService
{
    Task<CheckAvailabilityResponseDto> CheckAvailabilityAsync(
        CheckAvailabilityRequestDto request,
        CancellationToken cancellationToken);

    Task<ReservationResponseDto> CreateReservationAsync(
        Guid customerId,
        CreateReservationRequestDto request,
        CancellationToken cancellationToken);
}
