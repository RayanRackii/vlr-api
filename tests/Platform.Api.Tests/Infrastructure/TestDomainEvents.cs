using Platform.Api.DomainEvents;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Modules.Rentals.Services;
using Platform.Core.Domain.Events;

namespace Platform.Api.Tests.Infrastructure;

internal static class TestDomainEvents
{
    public static IDomainEventDispatcher<CatalogOrderEvent> AsDomainEvents(this ICatalogNotificationPublisher publisher) =>
        new DomainEventDispatcher<CatalogOrderEvent>([publisher]);

    public static IDomainEventDispatcher<ReservationEvent> AsDomainEvents(this IRentalsNotificationPublisher publisher) =>
        new DomainEventDispatcher<ReservationEvent>([publisher]);
}
