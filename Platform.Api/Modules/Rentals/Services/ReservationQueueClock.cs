using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Modules.Rentals.Services;

internal static class ReservationQueueClock
{
    public static readonly TimeSpan WaitingRoomLead = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan TurnDuration = TimeSpan.FromSeconds(90);

    public static DateTimeOffset OpensAt(DateOnly openingDate, TimeOnly openingTime) =>
        BrazilTimeZone.AtLocal(openingDate, openingTime);

    public static DateTimeOffset WaitingRoomOpensAt(DateOnly openingDate, TimeOnly openingTime) =>
        OpensAt(openingDate, openingTime) - WaitingRoomLead;

    /// <summary>
    /// OpeningDate D for status/join: today's civil date while before its waiting room
    /// (phase Closed); otherwise the unique D where WR(D) ≤ now &lt; WR(D+1).
    /// </summary>
    public static DateOnly ResolveOpeningDate(DateTimeOffset now, TimeOnly openingTime)
    {
        var today = BrazilTimeZone.GetCivilDate(now);
        var waitingRoomToday = WaitingRoomOpensAt(today, openingTime);
        if (now < waitingRoomToday)
        {
            return today;
        }

        var waitingRoomTomorrow = WaitingRoomOpensAt(today.AddDays(1), openingTime);
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
