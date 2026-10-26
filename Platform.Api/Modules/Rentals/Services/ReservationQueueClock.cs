using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Rentals.Services;

internal static class ReservationQueueClock
{
    public static readonly TimeSpan WaitingRoomLead = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan TurnDuration = TimeSpan.FromSeconds(90);

    public static DateTimeOffset OpensAt(DateOnly openingDate, TimeOnly openingTime, TimeZoneInfo? zone = null) =>
        TenantBookingClock.AtLocal(openingDate, openingTime, zone ?? TenantBookingClock.Resolve(null));

    public static DateTimeOffset WaitingRoomOpensAt(DateOnly openingDate, TimeOnly openingTime, TimeZoneInfo? zone = null) =>
        OpensAt(openingDate, openingTime, zone) - WaitingRoomLead;

    /// <summary>
    /// OpeningDate D for status/join: today's civil date while before its waiting room
    /// (phase Closed); otherwise the unique D where WR(D) ≤ now &lt; WR(D+1).
    /// </summary>
    public static DateOnly ResolveOpeningDate(DateTimeOffset now, TimeOnly openingTime, TimeZoneInfo? zone = null)
    {
        zone ??= TenantBookingClock.Resolve(null);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var waitingRoomToday = WaitingRoomOpensAt(today, openingTime, zone);
        if (now < waitingRoomToday)
        {
            return today;
        }

        var waitingRoomTomorrow = WaitingRoomOpensAt(today.AddDays(1), openingTime, zone);
        if (now < waitingRoomTomorrow)
        {
            return today;
        }

        return today.AddDays(1);
    }

    public static QueuePhase ResolvePhase(
        DateTimeOffset now,
        DateTimeOffset waitingRoomOpensAt,
        DateTimeOffset opensAt)
    {
        if (now < waitingRoomOpensAt)
        {
            return QueuePhase.Closed;
        }

        if (now < opensAt)
        {
            return QueuePhase.WaitingRoom;
        }

        return QueuePhase.Open;
    }
}
