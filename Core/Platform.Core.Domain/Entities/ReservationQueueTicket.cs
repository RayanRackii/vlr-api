using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// FIFO ticket in a <see cref="ReservationQueueSession"/>. Schema: rentals.
/// </summary>
public class ReservationQueueTicket : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid QueueSessionId { get; set; }

    public required Guid CustomerId { get; set; }

    public required long Sequence { get; set; }

    public required QueueTicketStatus Status { get; set; }

    public required DateTimeOffset JoinedAt { get; set; }

    public DateTimeOffset? TurnStartedAt { get; set; }

    public DateTimeOffset? TurnExpiresAt { get; set; }

    public Guid? CompletedReservationId { get; set; }

    public ReservationQueueSession QueueSession { get; set; } = null!;

    public Customer Customer { get; set; } = null!;

    public Reservation? CompletedReservation { get; set; }

    public void Activate(DateTimeOffset now, TimeSpan turnDuration)
    {
        Status = QueueTicketStatus.Active;
        TurnStartedAt = now;
        TurnExpiresAt = now + turnDuration;
        Touch();
    }

    public void Expire()
    {
        Status = QueueTicketStatus.Expired;
        Touch();
    }

    public void Complete(Guid reservationId)
    {
        Status = QueueTicketStatus.Completed;
        CompletedReservationId = reservationId;
        Touch();
    }

    public void Cancel()
    {
        Status = QueueTicketStatus.Cancelled;
        Touch();
    }
}
