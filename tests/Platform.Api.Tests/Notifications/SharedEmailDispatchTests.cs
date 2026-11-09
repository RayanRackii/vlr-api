using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Notifications;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Notifications;

public sealed class SharedEmailDispatchTests
{
    [Fact]
    public async Task Legacy_invite_handler_enqueues_both_alternatives()
    {
        var queue = new NotificationQueue();
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:FrontendBaseUrl"] = "https://example.invalid" }).Build();
        var handler = new Platform.Api.Features.InviteUser.DispatchWelcomeNotificationHandler(queue, configuration, new FakeHostEnvironment());
        await handler.Handle(new Platform.Api.Features.InviteUser.UserInvitedEvent("recipient@example.invalid", "synthetic"), default);
        var message = await queue.DequeueAsync(default);
        Assert.Equal("Convite Rolvix — defina sua senha", message.Subject);
        Assert.Contains("Aceitar convite", message.Body);
        Assert.Contains("https://example.invalid/invite?token=synthetic", message.TextBody);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queue_preserves_trusted_alternatives_and_encodes_generic_content(bool rendered)
    {
        const string plain = "Aviso <img src=x onerror=alert(1)>\nhttps://example.invalid/action";
        var content = rendered
            ? EmailContent.FromRenderedHtml(RolvixEmailLayout.Wrap("Pessoa Exemplo", RolvixEmailLayout.EmailVerificationBody("000000")))
            : EmailContent.FromPlainText(plain);
        var capture = new CaptureEmail();
        var queue = new NotificationQueue();
        var services = new ServiceCollection().AddSingleton<IEmailProvider>(capture).BuildServiceProvider();
        using var dispatcher = new NotificationDispatcherService(queue, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<NotificationDispatcherService>.Instance);
        await dispatcher.StartAsync(default);
        try
        {
            await queue.EnqueueAsync(new NotificationMessage("Email", "recipient@example.invalid", "Subject", rendered ? content.Html : plain)
                { TextBody = rendered ? content.Text : null });
            var sent = await capture.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(content.Text, sent.Text);
            Assert.Equal(content.Html, sent.Html);
            Assert.DoesNotContain("<img", sent.Html);
        }
        finally { await dispatcher.StopAsync(default); await services.DisposeAsync(); }
    }

    [Theory]
    [InlineData("catalog.order.rejected")]
    [InlineData("rentals.reservation.confirmed")]
    public async Task Outbox_passes_both_parts_without_losing_original_text(string eventType)
    {
        var tenantId = Guid.NewGuid();
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider { TenantId = tenantId });
        var payload = new Dictionary<string, string?>
        {
            [ReservationCustomerEmail.TenantNameKey] = "Empresa <Exemplo>",
            [ReservationCustomerEmail.CustomerNameKey] = "Pessoa <Exemplo>",
            [ReservationCustomerEmail.ReferenceKey] = "EXEMPLO-001",
            [ReservationCustomerEmail.UrlKey] = "https://example.invalid/reservation",
            ["reason"] = "Texto <script>alert(1)</script> & detalhe\nhttps://example.invalid/order",
        };
        var notification = new Notification
        {
            TenantId = tenantId, EventType = eventType, AggregateType = "Test", AggregateId = Guid.NewGuid(), Payload = payload,
        };
        var delivery = new NotificationDelivery
        {
            TenantId = tenantId, Notification = notification, NotificationId = notification.Id,
            Channel = NotificationChannel.Email, RecipientKind = NotificationRecipientKind.Customer,
            RecipientEmail = "recipient@example.invalid",
        };
        db.Notifications.Add(notification);
        db.NotificationDeliveries.Add(delivery);
        db.NotificationTemplates.Add(new NotificationTemplate
        {
            EventType = eventType, Channel = NotificationChannel.Email,
            SubjectTemplate = "Subject {{reason}}", BodyTemplate = "Conteúdo {{reason}}",
        });
        await db.SaveChangesAsync();
        var capture = new CaptureEmail();
        var processor = new NotificationOutboxProcessor(db, capture, null!, NullLogger<NotificationOutboxProcessor>.Instance, TimeProvider.System);
        await processor.ProcessDeliveryAsync(delivery.Id);
        Assert.Equal(NotificationDeliveryStatus.Sent, delivery.Status);
        var sent = await capture.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("<script>", sent.Html);
        Assert.Contains("<!DOCTYPE html>", sent.Html);
        if (eventType == CatalogEventTypes.OrderRejected)
        {
            Assert.Equal("Conteúdo " + payload["reason"], sent.Text);
            Assert.Contains("Empresa &lt;Exemplo&gt;", sent.Html);
            Assert.Equal("Subject " + payload["reason"], capture.Subject);
        }
        else
        {
            var expected = ReservationCustomerEmail.Render(eventType, payload);
            Assert.Equal(expected.TextBody, sent.Text);
            Assert.Equal(expected.HtmlBody, sent.Html);
            Assert.Equal(expected.FromDisplayName, capture.FromDisplayName);
        }
    }

    private sealed class CaptureEmail : IEmailProvider
    {
        public TaskCompletionSource<EmailContent> Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Subject { get; private set; }
        public string? FromDisplayName { get; private set; }
        public Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Callers must pass explicit alternatives.");
        public Task SendAsync(string recipient, string subject, EmailContent content, string? fromDisplayName, CancellationToken cancellationToken)
        {
            Subject = subject;
            FromDisplayName = fromDisplayName;
            Sent.TrySetResult(content);
            return Task.CompletedTask;
        }
    }
}
