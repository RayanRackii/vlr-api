using Platform.Api.Notifications;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Jobs;

public sealed class NotificationOutboxJob(
    INotificationOutboxProcessor processor,
    ILogger<NotificationOutboxJob> logger)
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Sweeping notification outbox.");
        await processor.ProcessDueAsync(cancellationToken);
    }
}
