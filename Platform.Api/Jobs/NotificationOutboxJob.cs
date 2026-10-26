using Platform.Api.Notifications;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Jobs;

public sealed class NotificationOutboxJob(
    INotificationOutboxProcessor processor,
    ILogger<NotificationOutboxJob> logger)
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var _ = PlatformScope.Begin(logger, "notification-outbox-sweep");
        logger.LogInformation("Sweeping notification outbox.");
        await processor.ProcessDueAsync(cancellationToken);
    }
}
