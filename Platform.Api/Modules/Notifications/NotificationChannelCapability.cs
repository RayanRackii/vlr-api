using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Notifications;

public sealed record NotificationChannelCapabilityEntry(
    NotificationChannel Channel,
    bool Configurable);

public sealed record NotificationEventCapability(
    string EventType,
    string ModuleKey,
    string DisplayKey,
    IReadOnlyList<NotificationChannelCapabilityEntry> Channels);

/// <summary>
/// Tenant-configurable notification channels per notifying event. SMS is never listed.
/// </summary>
public static class NotificationChannelCapability
{
    private static readonly NotificationChannelCapabilityEntry[] CatalogChannels =
    [
        new(NotificationChannel.InApp, Configurable: false),
        new(NotificationChannel.Email, Configurable: true),
        new(NotificationChannel.WhatsApp, Configurable: true),
    ];

    private static readonly NotificationChannelCapabilityEntry[] RentalsChannels =
    [
        new(NotificationChannel.WhatsApp, Configurable: true),
    ];

    public static readonly IReadOnlyList<NotificationEventCapability> Events =
    [
        Catalog(CatalogEventTypes.OrderCreated, "catalog.events.orderCreated"),
        Catalog(CatalogEventTypes.OrderApproved, "catalog.events.orderApproved"),
        Catalog(CatalogEventTypes.OrderReady, "catalog.events.orderReady"),
        Catalog(CatalogEventTypes.OrderRejected, "catalog.events.orderRejected"),
        Catalog(CatalogEventTypes.OrderCancelledBySupplier, "catalog.events.orderCancelledBySupplier"),
        Rentals(RentalEventTypes.ReservationPendingDeposit, "rentals.events.reservationPendingDeposit"),
        Rentals(RentalEventTypes.ReservationConfirmed, "rentals.events.reservationConfirmed"),
        Rentals(RentalEventTypes.ReservationCanceled, "rentals.events.reservationCanceled"),
        Rentals(RentalEventTypes.ReservationCompleted, "rentals.events.reservationCompleted"),
        Rentals(RentalEventTypes.ReservationReminder, "rentals.events.reservationReminder"),
    ];

    private static readonly IReadOnlyDictionary<string, NotificationEventCapability> EventsByType =
        Events.ToDictionary(item => item.EventType, StringComparer.Ordinal);

    public static IEnumerable<IGrouping<string, NotificationEventCapability>> GroupsInOrder =>
        Events.GroupBy(item => item.ModuleKey, StringComparer.Ordinal);

    public static bool TryGetEvent(string eventType, out NotificationEventCapability capability) =>
        EventsByType.TryGetValue(eventType, out capability!);

    public static bool TryGetChannel(
        string eventType,
        NotificationChannel channel,
        out NotificationChannelCapabilityEntry entry)
    {
        entry = null!;
        if (!TryGetEvent(eventType, out var eventCapability))
        {
            return false;
        }

        var match = eventCapability.Channels.FirstOrDefault(item => item.Channel == channel);
        if (match is null)
        {
            return false;
        }

        entry = match;
        return true;
    }

    private static NotificationEventCapability Catalog(string eventType, string displayKey) =>
        new(eventType, PlatformModules.Catalog, displayKey, CatalogChannels);

    private static NotificationEventCapability Rentals(string eventType, string displayKey) =>
        new(eventType, PlatformModules.Rentals, displayKey, RentalsChannels);
}
