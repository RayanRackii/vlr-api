using Platform.Api.Modules.Rentals.Dtos;
using Platform.Core.Domain.Entities;

namespace Platform.Api.Modules.Rentals.Services;

public interface IReservationQueueService
{
    Task<ReservationQueueStatusDto> GetStatusAsync(
        Guid rentalAssetId,
        Guid customerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Read-only roster for staff. Does not create a session and does not save ticket changes.
    /// </summary>
    Task<StaffReservationQueueDto> GetStaffRosterAsync(
        Guid rentalAssetId,
        DateOnly? openingDate,
        CancellationToken cancellationToken);

    Task<ReservationQueueStatusDto> JoinAsync(
        Guid rentalAssetId,
        Guid customerId,
        CancellationToken cancellationToken);

    Task<ReservationQueueStatusDto> LeaveAsync(
        Guid rentalAssetId,
        Guid customerId,
        CancellationToken cancellationToken);

    Task EnsureActiveTurnForBookingAsync(
        Guid customerId,
        RentalAsset rentalAsset,
        CancellationToken cancellationToken);

    Task CompleteTurnAsync(
        Guid customerId,
        RentalAsset rentalAsset,
        Guid reservationId,
        CancellationToken cancellationToken);
}
