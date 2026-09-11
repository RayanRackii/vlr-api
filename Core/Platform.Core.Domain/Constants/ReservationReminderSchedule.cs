namespace Platform.Core.Domain.Constants;

public static class ReservationReminderSchedule
{
    public static readonly TimeSpan LeadTime = TimeSpan.FromHours(24);

    public const string HangfireJobId = "rentals-reservation-reminder";

    public static bool IsInLeadWindow(DateTimeOffset start, DateTimeOffset utcNow) =>
        start > utcNow && start <= utcNow.Add(LeadTime);
}
