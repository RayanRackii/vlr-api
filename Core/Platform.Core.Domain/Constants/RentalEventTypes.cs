namespace Platform.Core.Domain.Constants;

public static class RentalEventTypes
{
    public const string ReservationPendingDeposit = "rentals.reservation.pending_deposit";
    public const string ReservationConfirmed = "rentals.reservation.confirmed";
    public const string ReservationCanceled = "rentals.reservation.canceled";
    public const string ReservationCompleted = "rentals.reservation.completed";
    public const string ReservationReminder = "rentals.reservation.reminder";

    public static readonly string[] Notifying =
    [
        ReservationPendingDeposit,
        ReservationConfirmed,
        ReservationCanceled,
        ReservationCompleted,
        ReservationReminder,
    ];
}
