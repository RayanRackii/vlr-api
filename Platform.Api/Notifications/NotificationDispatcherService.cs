using Microsoft.EntityFrameworkCore;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Notifications;

public sealed class NotificationDispatcherService(
    NotificationQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<NotificationDispatcherService> logger) : BackgroundService
{
    private const string EmailType = "Email";
    private const string WhatsAppType = "WhatsApp";
    private const string SmsType = "Sms";

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
                if (await ShouldSkipWhatsAppAsync(scope.ServiceProvider, message, cancellationToken))
                {
                    break;
                }

                var whatsAppProvider = scope.ServiceProvider.GetRequiredService<IWhatsAppProvider>();

                if (!string.IsNullOrWhiteSpace(message.TemplateName))
                {
                    await whatsAppProvider.SendTemplateAsync(
                        message.Recipient,
                        message.TemplateName,
                        message.TemplateLanguage ?? "pt_BR",
                        message.TemplateParameters ?? [],
                        cancellationToken);
                }
                else
                {
                    await whatsAppProvider.SendAsync(
                        message.Recipient,
                        message.Body,
                        cancellationToken);
                }

                break;
            }

            case SmsType:
            {
                var smsProvider = scope.ServiceProvider.GetRequiredService<ISmsProvider>();
                await smsProvider.SendAsync(
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

    private async Task<bool> ShouldSkipWhatsAppAsync(
        IServiceProvider services,
        NotificationMessage message,
        CancellationToken cancellationToken)
    {
        if (message.TenantId is not Guid tenantId)
        {
            return false;
        }

        var dbContext = services.GetRequiredService<AppDbContext>();
        var flags = await dbContext.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.NotificationsEmailOnly, t.IsTrial })
            .FirstOrDefaultAsync(cancellationToken);

        if (flags is null)
        {
            return false;
        }

        if (!flags.NotificationsEmailOnly && !flags.IsTrial)
        {
            return false;
        }

        logger.LogInformation(
            "Skipping WhatsApp for tenant {TenantId} (email-only/trial). Recipient {Recipient}.",
            tenantId,
            message.Recipient);
        return true;
    }
}
