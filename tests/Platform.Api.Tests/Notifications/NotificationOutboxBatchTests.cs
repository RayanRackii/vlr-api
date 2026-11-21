using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Notifications;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Notifications;

public sealed class NotificationOutboxBatchTests
{
    [Fact]
    public void Provider_timeout_is_short_and_stays_below_the_send_lease()
    {
        Assert.True(NotificationOutboxProcessor.ProviderCallTimeout <= TimeSpan.FromSeconds(15));
        Assert.True(NotificationOutboxProcessor.SendLease > NotificationOutboxProcessor.ProviderCallTimeout);
    }

    [Fact]
    public async Task Sweep_processes_at_most_one_batch_per_run_and_the_next_run_takes_the_rest()
    {
        var tenantId = Guid.NewGuid();
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider { TenantId = tenantId });
        db.NotificationTemplates.Add(new NotificationTemplate
        {
            EventType = "test.email",
            Channel = NotificationChannel.Email,
            BodyTemplate = "Body",
        });
        var total = NotificationOutboxProcessor.SweepBatchSize + 10;
        for (var i = 0; i < total; i++)
        {
            var notification = new Notification
            {
                TenantId = tenantId,
                EventType = "test.email",
                AggregateType = "Test",
                AggregateId = Guid.NewGuid(),
            };
            db.Notifications.Add(notification);
            db.NotificationDeliveries.Add(new NotificationDelivery
            {
                TenantId = tenantId,
                NotificationId = notification.Id,
                Notification = notification,
                Channel = NotificationChannel.Email,
                RecipientKind = NotificationRecipientKind.Customer,
                RecipientEmail = $"r{i}@example.test",
            });
        }

        await db.SaveChangesAsync();
        var email = new CountingEmailProvider();
        var processor = new NotificationOutboxProcessor(
            db,
            email,
            new UnusedWhatsApp(),
            NullLogger<NotificationOutboxProcessor>.Instance,
            TimeProvider.System);

        await processor.ProcessDueAsync();
        Assert.Equal(NotificationOutboxProcessor.SweepBatchSize, email.Calls);

        await processor.ProcessDueAsync();
        Assert.Equal(total, email.Calls);
        Assert.Equal(
            total,
            await db.NotificationDeliveries.CountAsync(d => d.Status == NotificationDeliveryStatus.Sent));
    }

    private sealed class CountingEmailProvider : IEmailProvider
    {
        public int Calls { get; private set; }

        public Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class UnusedWhatsApp : IWhatsAppProvider
    {
        public Task SendAsync(string recipient, string body, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected WhatsApp call.");

        public Task<string?> SendTemplateAsync(string recipient, string templateName, string languageCode,
            IReadOnlyList<string> bodyParameters, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected WhatsApp call.");
    }
}
