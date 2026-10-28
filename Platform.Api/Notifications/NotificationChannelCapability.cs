using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Notifications;

public sealed record NotificationChannelCapabilityEntry(
    NotificationChannel Channel,
    bool Configurable);

public sealed record NotificationEventCapability(
    string EventType,
    string ModuleKey,
    string DisplayKey,
    IReadOnlyList<NotificationChannelCapabilityEntry> Channels,
    string? StaffPermission = null,
    string? TargetRoute = null);

/// <summary>
/// The single generic source of per-event notification metadata, living in core notifications so
/// every module can read it without taking a cross-module dependency. Each event declares its module,
/// tenant-configurable channels (for the settings UI; SMS is never listed), the staff permission that
/// gates its in-app recipients, and the relative web route a staff notification deep-links to. Modules
/// only add rows here — no module-specific branching lives in shared code.
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
        new(NotificationChannel.Email, Configurable: true),
        new(NotificationChannel.WhatsApp, Configurable: true),
    ];

    private const string CatalogOrdersRoute = "/pedidos";
    private const string RentalsReservationsRoute = "/reservas";

    public static readonly IReadOnlyList<NotificationEventCapability> Events =
    [
        Catalog(CatalogEventTypes.OrderCreated, "catalog.events.orderCreated", $"{CatalogOrdersRoute}?status=pending"),
        Catalog(CatalogEventTypes.OrderApproved, "catalog.events.orderApproved", $"{CatalogOrdersRoute}?status=approved"),
        Catalog(CatalogEventTypes.OrderReady, "catalog.events.orderReady", $"{CatalogOrdersRoute}?status=ready"),
        Catalog(CatalogEventTypes.OrderRejected, "catalog.events.orderRejected", $"{CatalogOrdersRoute}?status=rejected"),
        Catalog(CatalogEventTypes.OrderCancelledBySupplier, "catalog.events.orderCancelledBySupplier", $"{CatalogOrdersRoute}?status=cancelled"),
        Catalog(CatalogEventTypes.OrderCancelledByCustomer, "catalog.events.orderCancelledByCustomer", $"{CatalogOrdersRoute}?status=cancelled"),
        Rentals(RentalEventTypes.ReservationPendingDeposit, "rentals.events.reservationPendingDeposit", RentalsReservationsRoute),
        Rentals(RentalEventTypes.ReservationConfirmed, "rentals.events.reservationConfirmed", RentalsReservationsRoute),
        Rentals(RentalEventTypes.ReservationCanceled, "rentals.events.reservationCanceled", RentalsReservationsRoute),
        Rentals(RentalEventTypes.ReservationCompleted, "rentals.events.reservationCompleted", RentalsReservationsRoute),
        Rentals(RentalEventTypes.ReservationReminder, "rentals.events.reservationReminder", RentalsReservationsRoute),
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

    private static NotificationEventCapability Catalog(string eventType, string displayKey, string targetRoute) =>
        new(eventType, PlatformModules.Catalog, displayKey, CatalogChannels,
            StaffPermission: Permissions.Catalog.OrdersManage, TargetRoute: targetRoute);

    private static NotificationEventCapability Rentals(string eventType, string displayKey, string targetRoute) =>
        new(eventType, PlatformModules.Rentals, displayKey, RentalsChannels,
            StaffPermission: Permissions.Rentals.ReservationsRead, TargetRoute: targetRoute);
}
