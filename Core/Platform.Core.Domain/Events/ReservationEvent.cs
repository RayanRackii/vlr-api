using Platform.Core.Domain.Entities;

namespace Platform.Core.Domain.Events;

public sealed record ReservationEvent(Reservation Reservation, string EventType) : IDomainEvent;
