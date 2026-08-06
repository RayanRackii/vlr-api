using Platform.Api.Modules.Rentals.Dtos;
using Platform.Core.Domain.Enums;

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

    Task<IReadOnlyList<ReservationResponseDto>> ListMineAsync(
        Guid customerId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ReservationResponseDto>> ListAdminAsync(
        DateOnly? from,
        DateOnly? to,
        ReservationStatus? status,
        Guid? assetId,
        CancellationToken cancellationToken);

    Task<ReservationResponseDto> ConfirmAsync(
        Guid reservationId,
        CancellationToken cancellationToken);

    Task<ReservationResponseDto> CancelAsync(
        Guid reservationId,
        CancellationToken cancellationToken);
}
