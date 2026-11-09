using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>
/// Shared queue decisions for the customer write path and the staff read.
/// The staff roster applies the same expiry, promotion, and position rules
/// without saving.
/// </summary>
internal static class ReservationQueueProjection
{
    internal readonly record struct Line(QueueTicketStatus Status, long Sequence);

    public static bool TurnIsDue(DateTimeOffset? turnExpiresAt, DateTimeOffset now) =>
        turnExpiresAt is { } expiresAt && expiresAt <= now;

    public static bool HasActive(IEnumerable<QueueTicketStatus> statuses) =>
        statuses.Any(status => status == QueueTicketStatus.Active);

    public static long? NextWaitingSequence(
        IEnumerable<Line> tickets,
        DateTimeOffset now,
        DateTimeOffset opensAt)
    {
        if (now < opensAt || tickets.Any(ticket => ticket.Status == QueueTicketStatus.Active))
        {
            return null;
        }

        long? next = null;
        foreach (var ticket in tickets)
        {
            if (ticket.Status != QueueTicketStatus.Waiting)
            {
                continue;
            }

            if (next is null || ticket.Sequence < next.Value)
            {
                next = ticket.Sequence;
            }
        }

        return next;
    }

    /// <summary>
    /// Waiting tickets are ranked by sequence. The Active ticket is position 1.
    /// Every other status is position 0, matching the customer ticket.
    /// </summary>
    public static int Position(
        QueueTicketStatus status,
        long sequence,
        IEnumerable<long> waitingSequences)
    {
        return status switch
        {
            QueueTicketStatus.Waiting => waitingSequences.Count(other => other < sequence) + 1,
            QueueTicketStatus.Active => 1,
            _ => 0,
        };
    }

    public static IReadOnlyList<ProjectedQueueTicket> Project(
        IReadOnlyList<ProjectedQueueTicket> tickets,
        DateTimeOffset now,
        DateTimeOffset opensAt)
    {
        var projected = new List<ProjectedQueueTicket>(tickets.Count);
        foreach (var ticket in tickets)
        {
            projected.Add(
                ticket.Status == QueueTicketStatus.Active && TurnIsDue(ticket.TurnExpiresAt, now)
                    ? ticket.Expire()
                    : ticket);
        }

        var nextSequence = NextWaitingSequence(
            projected.Select(ticket => new Line(ticket.Status, ticket.Sequence)),
            now,
            opensAt);
        if (nextSequence is not long sequence)
        {
            return projected;
        }

        var index = projected.FindIndex(ticket =>
            ticket.Status == QueueTicketStatus.Waiting && ticket.Sequence == sequence);
        if (index >= 0)
        {
            projected[index] = projected[index].Activate(now);
        }

        return projected;
    }
}

internal sealed record ProjectedQueueTicket(
    Guid Id,
    Guid CustomerId,
    string? CustomerName,
    QueueTicketStatus Status,
    long Sequence,
    DateTimeOffset JoinedAt,
    DateTimeOffset? TurnStartedAt,
    DateTimeOffset? TurnExpiresAt,
    Guid? CompletedReservationId)
{
    public ProjectedQueueTicket Expire() => this with { Status = QueueTicketStatus.Expired };

    public ProjectedQueueTicket Activate(DateTimeOffset now) => this with
    {
        Status = QueueTicketStatus.Active,
        TurnStartedAt = now,
        TurnExpiresAt = now + ReservationQueueClock.TurnDuration,
    };
}
