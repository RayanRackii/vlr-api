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

    internal static readonly TimeSpan ProviderCallTimeout = TimeSpan.FromSeconds(100);

    internal static readonly TimeSpan SendLease = TimeSpan.FromMinutes(5);

    private static readonly SemaphoreSlim InMemoryClaimGate = new(1, 1);

    public async Task ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var dueIds = await ClaimableDeliveries(now, externalOnly: true)
            .AsNoTracking()
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
        var delivery = await ClaimForProcessingAsync(deliveryId, cancellationToken);
        if (delivery is null)
        {
            return;
        }

        await DispatchClaimedAsync(delivery, cancellationToken);
    }

    public async Task ResendAsync(Guid deliveryId, CancellationToken cancellationToken = default)
    {
        var delivery = await ClaimForManualResendAsync(deliveryId, cancellationToken);
        if (delivery is null)
        {
            var visible = await dbContext.NotificationDeliveries
                .AsNoTracking()
                .AnyAsync(d => d.Id == deliveryId, cancellationToken);
            if (!visible)
            {
                throw new KeyNotFoundException("Delivery not found.");
            }

            throw new InvalidOperationException("Only failed deliveries can be resent.");
        }

        if (!NotificationRecipientContacts.AllowsExternalSend(
                delivery.Notification.EventType,
                delivery.Channel))
        {
            await FailWithoutProviderAsync(
                delivery,
                NotificationDeliveryFailures.Unrecoverable(delivery.Channel),
                cancellationToken);
            return;
        }

        if (NotificationDeliveryFailures.MissingContact(
                delivery.Channel,
                delivery.RecipientEmail,
                delivery.RecipientPhone) is { } missing)
        {
            await FailWithoutProviderAsync(delivery, missing, cancellationToken);
            return;
        }

        await DispatchClaimedAsync(delivery, cancellationToken);
    }

    private async Task DispatchClaimedAsync(
        NotificationDelivery delivery,
        CancellationToken cancellationToken)
    {
        if (delivery.Notification.EventType == RentalEventTypes.ReservationReminder
            && !await IsReminderStillDueAsync(delivery.Notification, cancellationToken))
        {
            await SuppressReminderAsync(delivery, attempt: null, budgetAttempt: 0, cancellationToken);
            return;
        }

        var budgetAttempt = delivery.AttemptCount + 1;
        var attempt = new NotificationDeliveryAttempt
        {
            DeliveryId = delivery.Id,
            AttemptNumber = NextHistoryNumber(delivery, budgetAttempt),
            StartedAt = DateTimeOffset.UtcNow,
        };

        if (delivery.Channel == NotificationChannel.Sms)
        {
            var smsError = NotificationDeliveryFailures.SmsUnavailable;
            attempt.Finish(NotificationAttemptOutcome.PermanentFailure, providerResponse: null, smsError);
            delivery.MarkFailed(smsError);
            RecordAttempt(delivery, attempt, budgetAttempt);
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        if (delivery.Channel is NotificationChannel.InApp or NotificationChannel.Push)
        {
            var skip = NotificationDeliveryFailures.NoExternalProvider;
            attempt.Finish(NotificationAttemptOutcome.PermanentFailure, providerResponse: null, skip);
            delivery.MarkFailed(skip);
            RecordAttempt(delivery, attempt, budgetAttempt);
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
            string? textBody = null;
            NotificationTemplate? template = null;
            if (reservationEmail)
            {
                var rendered = ReservationCustomerEmail.Render(
                    delivery.Notification.EventType,
                    delivery.Notification.Payload);
                body = rendered.HtmlBody;
                textBody = rendered.TextBody;
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
            await SuppressReminderAsync(delivery, attempt, budgetAttempt, cancellationToken);
            return;
        }

            switch (delivery.Channel)
            {
                case NotificationChannel.Email:
                    if (string.IsNullOrWhiteSpace(delivery.RecipientEmail))
                    {
                        throw new InvalidOperationException(NotificationDeliveryFailures.EmailMissing);
                    }

                    if (reservationEmail)
                    {
                        await emailProvider.SendAsync(
                            delivery.RecipientEmail,
                            subject,
                            new EmailContent(body, textBody!),
                            fromDisplayName,
                            cancellationToken);
                    }
                    else
                    {
                        await emailProvider.SendAsync(
                            delivery.RecipientEmail,
                            subject,
                            EmailContent.FromPlainText(body, delivery.Notification.Payload.GetValueOrDefault("tenantName")),
                            cancellationToken);
                    }

                    break;
                case NotificationChannel.WhatsApp:
                    if (string.IsNullOrWhiteSpace(delivery.RecipientPhone))
                    {
                        throw new WhatsAppSendException(
                            NotificationDeliveryFailures.WhatsAppMissing,
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
                    RecordAttempt(delivery, attempt, budgetAttempt);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    return;
                default:
                    throw new InvalidOperationException($"Unsupported channel {delivery.Channel}.");
            }

            attempt.Finish(NotificationAttemptOutcome.Success, providerResponse: "ok", errorMessage: null);
            delivery.MarkSent(providerMessageId: null);
            RecordAttempt(delivery, attempt, budgetAttempt);
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
            FinishFailedAttempt(delivery, attempt, budgetAttempt, ex, isTransient: true);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FinishFailedAttempt(delivery, attempt, budgetAttempt, ex, IsTransient(ex));
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private IQueryable<NotificationDelivery> ClaimableDeliveries(DateTimeOffset now, bool externalOnly)
    {
        var staleBefore = now - SendLease;
        var query = dbContext.NotificationDeliveries.Where(delivery =>
            (delivery.Status == NotificationDeliveryStatus.Queued
                && (delivery.NextAttemptAt == null || delivery.NextAttemptAt <= now))
            || (delivery.Status == NotificationDeliveryStatus.Sending
                && (delivery.UpdatedAt == null || delivery.UpdatedAt <= staleBefore)));
        if (externalOnly)
        {
            query = query.Where(delivery => delivery.Channel != NotificationChannel.InApp);
        }

        return query;
    }

    private async Task<NotificationDelivery?> ClaimForProcessingAsync(
        Guid deliveryId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (dbContext.Database.IsRelational())
        {
            var claimed = await ClaimableDeliveries(now, externalOnly: false)
                .Where(delivery => delivery.Id == deliveryId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(delivery => delivery.Status, NotificationDeliveryStatus.Sending)
                        .SetProperty(delivery => delivery.UpdatedAt, now),
                    cancellationToken);
            if (claimed != 1)
            {
                return null;
            }

            DetachDelivery(deliveryId);
            return await LoadDeliveryAsync(deliveryId, cancellationToken);
        }

        await InMemoryClaimGate.WaitAsync(cancellationToken);
        try
        {
            var delivery = await LoadDeliveryAsync(deliveryId, cancellationToken);
            if (delivery is null || !delivery.TryClaimForProcessing(now, SendLease))
            {
                return null;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return delivery;
        }
        finally
        {
            InMemoryClaimGate.Release();
        }
    }

    private async Task<NotificationDelivery?> ClaimForManualResendAsync(
        Guid deliveryId,
        CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsRelational())
        {
            await InMemoryClaimGate.WaitAsync(cancellationToken);
            try
            {
                var delivery = await LoadDeliveryAsync(deliveryId, cancellationToken);
                if (delivery is null || delivery.Status != NotificationDeliveryStatus.Failed)
                {
                    return null;
                }

                var (liveEmail, livePhone) = await NotificationRecipientContacts.ResolveForResendAsync(
                    dbContext,
                    delivery,
                    cancellationToken);
                if (!delivery.TryClaimForResend(DateTimeOffset.UtcNow, liveEmail, livePhone))
                {
                    return null;
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                return delivery;
            }
            finally
            {
                InMemoryClaimGate.Release();
            }
        }

        var current = await dbContext.NotificationDeliveries
            .AsNoTracking()
            .Include(delivery => delivery.Notification)
            .FirstOrDefaultAsync(delivery => delivery.Id == deliveryId, cancellationToken);
        if (current is null || current.Status != NotificationDeliveryStatus.Failed)
        {
            return null;
        }

        var resolved = await NotificationRecipientContacts.ResolveForResendAsync(
            dbContext,
            current,
            cancellationToken);
        var email = resolved.Email;
        var phone = resolved.Phone;
        var now = DateTimeOffset.UtcNow;
        var observedUpdatedAt = current.UpdatedAt;
        var claim = dbContext.NotificationDeliveries.Where(delivery =>
            delivery.Id == deliveryId && delivery.Status == NotificationDeliveryStatus.Failed);
        claim = observedUpdatedAt is { } version
            ? claim.Where(delivery => delivery.UpdatedAt == version)
            : claim.Where(delivery => delivery.UpdatedAt == null);
        var updated = await claim
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(delivery => delivery.Status, NotificationDeliveryStatus.Sending)
                    .SetProperty(delivery => delivery.AttemptCount, 0)
                    .SetProperty(delivery => delivery.ErrorMessage, (string?)null)
                    .SetProperty(delivery => delivery.NextAttemptAt, now)
                    .SetProperty(delivery => delivery.RecipientEmail, email)
                    .SetProperty(delivery => delivery.RecipientPhone, phone)
                    .SetProperty(delivery => delivery.UpdatedAt, now),
                cancellationToken);
        if (updated != 1)
        {
            return null;
        }

        DetachDelivery(deliveryId);
        return await LoadDeliveryAsync(deliveryId, cancellationToken);
    }

    private async Task FailWithoutProviderAsync(
        NotificationDelivery delivery,
        string error,
        CancellationToken cancellationToken)
    {
        var budgetAttempt = delivery.AttemptCount + 1;
        var attempt = new NotificationDeliveryAttempt
        {
            DeliveryId = delivery.Id,
            AttemptNumber = NextHistoryNumber(delivery, budgetAttempt),
            StartedAt = DateTimeOffset.UtcNow,
        };
        attempt.Finish(NotificationAttemptOutcome.PermanentFailure, providerResponse: null, error);
        RecordAttempt(delivery, attempt, budgetAttempt);
        delivery.MarkFailed(error);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private Task<NotificationDelivery?> LoadDeliveryAsync(Guid deliveryId, CancellationToken cancellationToken) =>
        dbContext.NotificationDeliveries
            .Include(delivery => delivery.Notification)
            .Include(delivery => delivery.Attempts)
            .FirstOrDefaultAsync(delivery => delivery.Id == deliveryId, cancellationToken);

    private void DetachDelivery(Guid deliveryId)
    {
        foreach (var entry in dbContext.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is NotificationDelivery delivery && delivery.Id == deliveryId)
            {
                entry.State = EntityState.Detached;
            }
            else if (entry.Entity is NotificationDeliveryAttempt attempt && attempt.DeliveryId == deliveryId)
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    private static void RecordAttempt(
        NotificationDelivery delivery,
        NotificationDeliveryAttempt attempt,
        int budgetAttempt)
    {
        delivery.AttemptCount = budgetAttempt;
        if (!delivery.Attempts.Contains(attempt))
        {
            delivery.AddAttempt(attempt);
        }
    }

    private static int NextHistoryNumber(NotificationDelivery delivery, int budgetAttempt)
    {
        var max = 0;
        foreach (var existing in delivery.Attempts)
        {
            if (existing.AttemptNumber > max)
            {
                max = existing.AttemptNumber;
            }
        }

        return Math.Max(max + 1, budgetAttempt);
    }

    private async Task SuppressReminderAsync(
        NotificationDelivery delivery,
        NotificationDeliveryAttempt? attempt,
        int budgetAttempt,
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
            RecordAttempt(delivery, attempt, budgetAttempt);
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
        RecordAttempt(delivery, attempt, attemptNumber);

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
