using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Platform.Api.Notifications;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Notifications;

public sealed class NotificationOutboxDeferralTests
{
    [Theory]
    [InlineData("2099-12-31T23:59:59Z", "2100-01-01T00:00:00Z", 0)]
    [InlineData("2099-10-25T00:00:00Z", "2099-10-26T00:00:00Z", 4)]
    public async Task Daily_quota_defers_to_utc_midnight_without_consuming_attempts_and_can_resume(
        string now, string nextMidnight, int priorAttempts)
    {
        var tenantId = Guid.NewGuid();
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider { TenantId = tenantId });
        var notification = new Notification
        {
            TenantId = tenantId,
            EventType = "test.email",
            AggregateType = "Test",
            AggregateId = Guid.NewGuid(),
        };
        var delivery = new NotificationDelivery
        {
            TenantId = tenantId,
            NotificationId = notification.Id,
            Notification = notification,
            Channel = NotificationChannel.Email,
            RecipientKind = NotificationRecipientKind.Customer,
            RecipientEmail = "recipient@example.test",
            AttemptCount = priorAttempts,
        };
        db.Notifications.Add(notification);
        db.NotificationDeliveries.Add(delivery);
        db.NotificationTemplates.Add(new NotificationTemplate
        {
            EventType = notification.EventType,
            Channel = NotificationChannel.Email,
            BodyTemplate = "Body",
        });
        await db.SaveChangesAsync();

        var clock = new TestTimeProvider(DateTimeOffset.Parse(now));
        var email = new QuotaEmailProvider();
        var logger = new RecordingLogger();
        var processor = new NotificationOutboxProcessor(db, email, new UnusedWhatsAppProvider(), logger, clock);

        await processor.ProcessDeliveryAsync(delivery.Id);
        db.ChangeTracker.Clear();
        var deferred = await db.NotificationDeliveries.Include(d => d.Attempts).SingleAsync();
        Assert.Equal(NotificationDeliveryStatus.Queued, deferred.Status);
        Assert.Equal(priorAttempts, deferred.AttemptCount);
        Assert.Equal(DateTimeOffset.Parse(nextMidnight), deferred.NextAttemptAt);
        Assert.Null(deferred.ErrorMessage);
        Assert.Empty(deferred.Attempts);
        Assert.Empty(await db.NotificationDeliveryAttempts.ToListAsync());
        var warning = Assert.Single(logger.Warnings);
        Assert.DoesNotContain("recipient", warning);
        Assert.DoesNotContain("example.test", warning);

        // Both entry points honor the persisted future deadline.
        await processor.ProcessDueAsync();
        await processor.ProcessDeliveryAsync(delivery.Id);
        Assert.Equal(1, email.Calls);
        Assert.Single(logger.Warnings);

        clock.SetUtcNow(DateTimeOffset.Parse(nextMidnight));
        // Make the delivery due without advancing the system clock used by queue polling.
        deferred.NextAttemptAt = null;
        await db.SaveChangesAsync();
        await processor.ProcessDeliveryAsync(delivery.Id);
        Assert.Equal(2, email.Calls);
        Assert.Equal(priorAttempts, deferred.AttemptCount);
        Assert.Equal(DateTimeOffset.Parse(nextMidnight).AddDays(1), deferred.NextAttemptAt);
        Assert.Empty(await db.NotificationDeliveryAttempts.ToListAsync());

        email.QuotaExceeded = false;
        clock.Advance(TimeSpan.FromDays(1));
        deferred.NextAttemptAt = null;
        await db.SaveChangesAsync();
        await processor.ProcessDeliveryAsync(delivery.Id);
        db.ChangeTracker.Clear();
        var sent = await db.NotificationDeliveries.Include(d => d.Attempts).SingleAsync();
        Assert.Equal(NotificationDeliveryStatus.Sent, sent.Status);
        Assert.Equal(priorAttempts + 1, sent.AttemptCount);
        Assert.Null(sent.NextAttemptAt);
        Assert.Equal(priorAttempts + 1, Assert.Single(sent.Attempts).AttemptNumber);
    }

    private sealed class QuotaEmailProvider : IEmailProvider
    {
        public int Calls { get; private set; }
        public bool QuotaExceeded { get; set; } = true;

        public Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken = default)
        {
            Calls++;
            return QuotaExceeded ? Task.FromException(new EmailDeferredException()) : Task.CompletedTask;
        }
    }

    private sealed class UnusedWhatsAppProvider : IWhatsAppProvider
    {
        public Task SendAsync(string recipient, string body, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected WhatsApp call.");

        public Task<string?> SendTemplateAsync(string recipient, string templateName, string languageCode,
            IReadOnlyList<string> bodyParameters, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected WhatsApp call.");
    }

    private sealed class RecordingLogger : ILogger<NotificationOutboxProcessor>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
