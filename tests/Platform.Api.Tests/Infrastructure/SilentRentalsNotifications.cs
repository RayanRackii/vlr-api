using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Notifications;

namespace Platform.Api.Tests.Infrastructure;

internal static class SilentRentalsNotifications
{
    public static NoOpRentalsNotificationPublisher Publisher { get; } = new();

    public static NoOpNotificationOutboxScheduler Scheduler { get; } = new();
}
