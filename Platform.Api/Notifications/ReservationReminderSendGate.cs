using Platform.Core.Domain.Enums;

namespace Platform.Api.Notifications;

/// <summary>
/// Send-time rule for <c>rentals.reservation.reminder</c>.
/// The minute sweep enqueues while the reservation is still active; cancel can commit before the outbox runs.
/// </summary>
public static class ReservationReminderSendGate
{
    public const string SkippedMessage =
        "Reminder skipped because the reservation is not active or has already started.";

    public static bool ShouldSend(ReservationStatus status, DateTimeOffset start, DateTimeOffset utcNow) =>
        status is ReservationStatus.PendingDeposit or ReservationStatus.Confirmed
        && start > utcNow;
}
