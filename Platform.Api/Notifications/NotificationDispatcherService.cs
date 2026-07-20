namespace Platform.Api.Notifications;

public sealed class NotificationDispatcherService(
    NotificationQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<NotificationDispatcherService> logger) : BackgroundService
{
    private const string EmailType = "Email";
    private const string WhatsAppType = "WhatsApp";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Notification dispatcher started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var message = await queue.DequeueAsync(stoppingToken);
                await DispatchAsync(message, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to dispatch a notification message.");
            }
        }

        logger.LogInformation("Notification dispatcher stopped.");
    }

    private async Task DispatchAsync(
        NotificationMessage message,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();

        switch (message.Type)
        {
            case EmailType:
            {
                var emailProvider = scope.ServiceProvider.GetRequiredService<IEmailProvider>();
                await emailProvider.SendAsync(
                    message.Recipient,
                    message.Subject,
                    message.Body,
                    cancellationToken);
                break;
            }

            case WhatsAppType:
            {
                var whatsAppProvider = scope.ServiceProvider.GetRequiredService<IWhatsAppProvider>();
                await whatsAppProvider.SendAsync(
                    message.Recipient,
                    message.Body,
                    cancellationToken);
                break;
            }

            default:
                logger.LogWarning(
                    "Unknown notification type '{Type}' for recipient {Recipient}.",
                    message.Type,
                    message.Recipient);
                break;
        }
    }
}
