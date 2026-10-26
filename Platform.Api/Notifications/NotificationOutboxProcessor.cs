using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Notifications;

public sealed class NotificationOutboxProcessor(
    AppDbContext dbContext,
    IEmailProvider emailProvider,
    IWhatsAppProvider whatsAppProvider,
    ILogger<NotificationOutboxProcessor> logger,
    TimeProvider timeProvider) : INotificationOutboxProcessor
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
        // Hangfire runs this alone (no tenant). Lookup is by delivery id; the reminder check
        // below still pins the notification's own tenant.
        using var _ = PlatformScope.Begin(logger, "notification-outbox-delivery");
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

        if (delivery.Notification.EventType == RentalEventTypes.ReservationReminder
            && !await IsReminderStillDueAsync(delivery.Notification, cancellationToken))
        {
            await SuppressReminderAsync(delivery, attempt: null, cancellationToken);
            return;
        }

        var attemptNumber = delivery.AttemptCount + 1;
        var attempt = new NotificationDeliveryAttempt
        {
            DeliveryId = delivery.Id,
            AttemptNumber = attemptNumber,
            StartedAt = DateTimeOffset.UtcNow,
        };

        if (delivery.Channel == NotificationChannel.Sms)
        {
            const string smsError = "SMS channel is not available.";
            attempt.Finish(NotificationAttemptOutcome.PermanentFailure, providerResponse: null, smsError);
            delivery.MarkFailed(smsError);
            RecordAttempt(delivery, attempt);
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        if (delivery.Channel is NotificationChannel.InApp or NotificationChannel.Push)
        {
            const string skip = "Channel has no external provider.";
            attempt.Finish(NotificationAttemptOutcome.PermanentFailure, providerResponse: null, skip);
            delivery.MarkFailed(skip);
            RecordAttempt(delivery, attempt);
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        try
        {
            var reservationEmail = delivery.Channel == NotificationChannel.Email
                && ReservationCustomerEmail.Applies(delivery.Notification.EventType);

            string body;
            string subject;
            string? fromDisplayName = null;
            NotificationTemplate? template = null;
            if (reservationEmail)
            {
                var rendered = ReservationCustomerEmail.Render(
                    delivery.Notification.EventType,
                    delivery.Notification.Payload);
                body = rendered.HtmlBody;
                subject = rendered.Subject;
                fromDisplayName = rendered.FromDisplayName;
            }
            else
            {
                template = await dbContext.NotificationTemplates
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

                body = Render(template.BodyTemplate, delivery.Notification.Payload);
                subject = Render(template.SubjectTemplate ?? string.Empty, delivery.Notification.Payload);
            }

            // Template lookup can outlive the cancel. Read the reservation again before the provider call.
            if (delivery.Notification.EventType == RentalEventTypes.ReservationReminder
                && !await IsReminderStillDueAsync(delivery.Notification, cancellationToken))
            {
                await SuppressReminderAsync(delivery, attempt, cancellationToken);
                return;
            }

            switch (delivery.Channel)
            {
                case NotificationChannel.Email:
                    if (string.IsNullOrWhiteSpace(delivery.RecipientEmail))
                    {
                        throw new InvalidOperationException("Email recipient is missing.");
                    }

                    if (reservationEmail)
                    {
                        await emailProvider.SendAsync(
                            delivery.RecipientEmail,
                            subject,
                            body,
                            fromDisplayName,
                            cancellationToken);
                    }
                    else
                    {
                        await emailProvider.SendAsync(
                            delivery.RecipientEmail,
                            subject,
                            body,
                            cancellationToken);
                    }

                    break;
                case NotificationChannel.WhatsApp:
                    if (string.IsNullOrWhiteSpace(delivery.RecipientPhone))
                    {
                        throw new WhatsAppSendException(
                            "WhatsApp recipient is missing.",
                            isTransient: false);
                    }

                    if (template is null || string.IsNullOrWhiteSpace(template.WhatsAppTemplateName))
                    {
                        throw new WhatsAppSendException(
                            $"Approved WhatsApp template is not configured for {delivery.Notification.EventType}.",
                            isTransient: false);
                    }

                    var providerMessageId = await whatsAppProvider.SendTemplateAsync(
                        delivery.RecipientPhone,
                        template.WhatsAppTemplateName,
                        "pt_BR",
                        WhatsAppParameterBinder.Bind(
                            delivery.Notification.EventType,
                            delivery.Notification.Payload),
                        cancellationToken);

                    attempt.Finish(
                        NotificationAttemptOutcome.Success,
                        providerResponse: Truncate(providerMessageId, 200),
                        errorMessage: null);
                    delivery.MarkSent(providerMessageId);
                    RecordAttempt(delivery, attempt);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    return;
                default:
                    throw new InvalidOperationException($"Unsupported channel {delivery.Channel}.");
            }

            attempt.Finish(NotificationAttemptOutcome.Success, providerResponse: "ok", errorMessage: null);
            delivery.MarkSent(providerMessageId: null);
            RecordAttempt(delivery, attempt);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (EmailDeferredException) when (delivery.Channel == NotificationChannel.Email)
        {
            var nextMidnight = new DateTimeOffset(timeProvider.GetUtcNow().UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
            delivery.MarkQueuedForRetry(nextMidnight);
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogWarning(
                "Email delivery deferred: provider daily quota exceeded. Next attempt at {NextAttemptAt}.",
                nextMidnight);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            FinishFailedAttempt(delivery, attempt, attemptNumber, ex, isTransient: true);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FinishFailedAttempt(delivery, attempt, attemptNumber, ex, IsTransient(ex));
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private static void RecordAttempt(NotificationDelivery delivery, NotificationDeliveryAttempt attempt)
    {
        delivery.AttemptCount = attempt.AttemptNumber;
        if (!delivery.Attempts.Contains(attempt))
        {
            delivery.AddAttempt(attempt);
        }
    }

    private async Task SuppressReminderAsync(
        NotificationDelivery delivery,
        NotificationDeliveryAttempt? attempt,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Skipping reminder delivery {DeliveryId} for reservation {ReservationId}.",
            delivery.Id,
            delivery.Notification.AggregateId);
        attempt?.Finish(
            NotificationAttemptOutcome.PermanentFailure,
            providerResponse: null,
            ReservationReminderSendGate.SkippedMessage);
        if (attempt is not null)
        {
            RecordAttempt(delivery, attempt);
        }
        delivery.MarkFailed(ReservationReminderSendGate.SkippedMessage);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> IsReminderStillDueAsync(
        Notification notification,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(notification.AggregateType, "Reservation", StringComparison.Ordinal))
        {
            return false;
        }

        // Hangfire has no ambient tenant, so the global filter is off. Ignore it explicitly and
        // match the notification's own tenant so a different ambient tenant cannot hide the row.
        var reservation = await dbContext.Reservations
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(r => r.Id == notification.AggregateId && r.TenantId == notification.TenantId)
            .Select(r => new { r.Status, r.StartDateTime })
            .FirstOrDefaultAsync(cancellationToken);

        if (reservation is null)
        {
            return false;
        }

        return ReservationReminderSendGate.ShouldSend(
            reservation.Status,
            reservation.StartDateTime,
            timeProvider.GetUtcNow());
    }

    private void FinishFailedAttempt(
        NotificationDelivery delivery,
        NotificationDeliveryAttempt attempt,
        int attemptNumber,
        Exception ex,
        bool isTransient)
    {
        var outcome = isTransient
            ? NotificationAttemptOutcome.TransientFailure
            : NotificationAttemptOutcome.PermanentFailure;
        attempt.Finish(outcome, providerResponse: Truncate(ex.Message, 200), ex.Message);
        RecordAttempt(delivery, attempt);

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
    }

    private static bool IsTransient(Exception ex) =>
        ex switch
        {
            WhatsAppSendException whatsApp => whatsApp.IsTransient,
            HttpRequestException => true,
            TimeoutException => true,
            TaskCanceledException => true,
            _ => false,
        };

    private static string Render(string template, IReadOnlyDictionary<string, string?> payload)
    {
        var result = template;
        foreach (var (key, value) in payload)
        {
            result = result.Replace("{{" + key + "}}", value ?? string.Empty, StringComparison.Ordinal);
        }

        return result;
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value;
        }

        return value[..max];
    }
}
