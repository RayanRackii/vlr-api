using Platform.Core.Domain.Entities;

namespace Platform.Core.Domain.Events;

public sealed record CatalogOrderEvent(CatalogOrder Order, string EventType) : IDomainEvent;
