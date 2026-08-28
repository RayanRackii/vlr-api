using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Notifications;

public sealed class NotificationOutboxProcessor(
    AppDbContext dbContext,
    IEmailProvider emailProvider,
    IWhatsAppProvider whatsAppProvider,
    ILogger<NotificationOutboxProcessor> logger) : INotificationOutboxProcessor
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(6),
    ];

    private const int MaxAttempts = 5;

    public async Task ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var dueIds = await dbContext.NotificationDeliveries
            .AsNoTracking()
            .Where(d => d.Status == NotificationDeliveryStatus.Queued
                        && (d.NextAttemptAt == null || d.NextAttemptAt <= now)
                        && d.Channel != NotificationChannel.InApp)
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);

        foreach (var id in dueIds)
        {
            await ProcessDeliveryAsync(id, cancellationToken);
        }
    }

    public async Task ProcessDeliveryAsync(
        Guid deliveryId,
        CancellationToken cancellationToken = default)
    {
        var delivery = await dbContext.NotificationDeliveries
            .Include(d => d.Notification)
            .Include(d => d.Attempts)
            .FirstOrDefaultAsync(d => d.Id == deliveryId, cancellationToken);

        if (delivery is null)
        {
            return;
        }

        if (delivery.Status is NotificationDeliveryStatus.Sent or NotificationDeliveryStatus.Delivered)
        {
            return;
        }

        if (delivery.Status != NotificationDeliveryStatus.Queued)
        {
            return;
        }

        if (delivery.NextAttemptAt is { } next && next > DateTimeOffset.UtcNow)
        {
            return;
        }

        var attemptNumber = delivery.AttemptCount + 1;
        delivery.AttemptCount = attemptNumber;
        var attempt = new NotificationDeliveryAttempt
        {
            DeliveryId = delivery.Id,
            AttemptNumber = attemptNumber,
            StartedAt = DateTimeOffset.UtcNow,
        };
        delivery.AddAttempt(attempt);

        if (delivery.Channel == NotificationChannel.Sms)
        {
            const string smsError = "SMS channel is not available.";
            attempt.Finish(NotificationAttemptOutcome.PermanentFailure, providerResponse: null, smsError);
            delivery.MarkFailed(smsError);
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        if (delivery.Channel is NotificationChannel.InApp or NotificationChannel.Push)
        {
            const string skip = "Channel has no external provider.";
            attempt.Finish(NotificationAttemptOutcome.PermanentFailure, providerResponse: null, skip);
            delivery.MarkFailed(skip);
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        try
        {
            var template = await dbContext.NotificationTemplates
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    t => t.EventType == delivery.Notification.EventType
                         && t.Channel == delivery.Channel
                         && t.Language == "pt-BR"
                         && t.IsActive,
                    cancellationToken);

            if (template is null)
            {
                throw new InvalidOperationException(
                    $"No active template for {delivery.Notification.EventType}/{delivery.Channel}.");
            }

            var body = Render(template.BodyTemplate, delivery.Notification.Payload);
            var subject = Render(template.SubjectTemplate ?? string.Empty, delivery.Notification.Payload);

            switch (delivery.Channel)
            {
                case NotificationChannel.Email:
                    if (string.IsNullOrWhiteSpace(delivery.RecipientEmail))
                    {
                        throw new InvalidOperationException("Email recipient is missing.");
                    }

                    await emailProvider.SendAsync(
                        delivery.RecipientEmail,
                        subject,
                        body,
                        cancellationToken);
                    break;
                case NotificationChannel.WhatsApp:
                    if (string.IsNullOrWhiteSpace(delivery.RecipientPhone))
                    {
                        throw new InvalidOperationException("WhatsApp recipient is missing.");
                    }

                    if (!string.IsNullOrWhiteSpace(template.WhatsAppTemplateName))
                    {
                        await whatsAppProvider.SendTemplateAsync(
                            delivery.RecipientPhone,
                            template.WhatsAppTemplateName,
                            "pt_BR",
                            ExtractWhatsAppParameters(delivery.Notification.Payload),
                            cancellationToken);
                    }
                    else
                    {
                        await whatsAppProvider.SendAsync(
                            delivery.RecipientPhone,
                            body,
                            cancellationToken);
                    }

                    break;
                default:
                    throw new InvalidOperationException($"Unsupported channel {delivery.Channel}.");
            }

            attempt.Finish(NotificationAttemptOutcome.Success, providerResponse: "ok", errorMessage: null);
            delivery.MarkSent(providerMessageId: null);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var outcome = IsTransient(ex)
                ? NotificationAttemptOutcome.TransientFailure
                : NotificationAttemptOutcome.PermanentFailure;
            attempt.Finish(outcome, providerResponse: null, ex.Message);

            if (outcome == NotificationAttemptOutcome.PermanentFailure || attemptNumber >= MaxAttempts)
            {
                delivery.MarkFailed(ex.Message);
            }
            else
            {
                var delay = RetryDelays[Math.Min(attemptNumber - 1, RetryDelays.Length - 1)];
                delivery.MarkQueuedForRetry(DateTimeOffset.UtcNow.Add(delay));
            }

            logger.LogWarning(
                ex,
                "Notification delivery {DeliveryId} attempt {Attempt} failed ({Outcome}).",
                delivery.Id,
                attemptNumber,
                outcome);

            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or TimeoutException or TaskCanceledException;

    private static string Render(string template, IReadOnlyDictionary<string, string?> payload)
    {
        var result = template;
        foreach (var (key, value) in payload)
        {
            result = result.Replace("{{" + key + "}}", value ?? string.Empty, StringComparison.Ordinal);
        }

        return result;
    }

    private static IReadOnlyList<string> ExtractWhatsAppParameters(
        IReadOnlyDictionary<string, string?> payload)
    {
        string[] keys = ["tenantName", "customerName", "orderNumber", "orderStatus"];
        return keys.Select(key => payload.GetValueOrDefault(key) ?? string.Empty).ToArray();
    }
}
