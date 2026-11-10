using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Notifications;
using Platform.Api.Tests.Catalog;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Notifications;

public sealed class NotificationResendTests : IClassFixture<PostgresContainerFixture>
{
    private const string EventType = "catalog.order.created";
    private const string LivePhone = "+5511988887777";
    private const string StalePhone = "+5500000000000";

    private readonly PostgresContainerFixture _postgres;

    public NotificationResendTests(PostgresContainerFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task Missing_phone_stays_failed_and_does_not_call_the_provider()
    {
        await using var harness = await CatalogHarness.CreateAsync();
        var customer = AddCustomer(harness, phone: null);
        var delivery = SeedFailedWhatsApp(harness, customer.Id, NotificationRecipientKind.Customer, StalePhone, attemptCount: 4);
        AddWhatsAppTemplate(harness);
        await harness.Db.SaveChangesAsync();

        var resent = await harness.Notifications.ResendAsync(delivery.Id, CancellationToken.None);

        Assert.Equal(NotificationDeliveryStatus.Failed, resent.Status);
        Assert.Equal(NotificationDeliveryFailures.WhatsAppMissing, resent.ErrorMessage);
        Assert.True(resent.CanResend);
        Assert.Equal(1, resent.AttemptCount);
        Assert.Equal(0, harness.WhatsApp.SendCount);
        Assert.Equal(2, resent.Attempts.Count);
        Assert.Equal(2, resent.Attempts[1].AttemptNumber);
        Assert.Equal(NotificationChannel.WhatsApp, resent.Attempts[1].Channel);
        Assert.Equal(NotificationDeliveryFailures.WhatsAppMissing, resent.Attempts[1].ErrorMessage);
        Assert.Equal(NotificationAttemptOutcome.PermanentFailure, resent.Attempts[1].Outcome);
        Assert.Equal(2, await harness.Db.NotificationDeliveryAttempts.CountAsync(a => a.DeliveryId == delivery.Id));
    }

    [Fact]
    public async Task Contact_added_then_resend_sends_the_live_phone()
    {
        await using var harness = await CatalogHarness.CreateAsync();
        var customer = AddCustomer(harness, LivePhone);
        var delivery = SeedFailedWhatsApp(harness, customer.Id, NotificationRecipientKind.Customer, snapshotPhone: null, attemptCount: 3);
        AddWhatsAppTemplate(harness);
        await harness.Db.SaveChangesAsync();

        var resent = await harness.Notifications.ResendAsync(delivery.Id, CancellationToken.None);

        Assert.Equal(NotificationDeliveryStatus.Sent, resent.Status);
        Assert.Null(resent.ErrorMessage);
        Assert.False(resent.CanResend);
        Assert.Equal(1, resent.AttemptCount);
        Assert.Equal(1, harness.WhatsApp.SendCount);
        Assert.Equal(LivePhone, harness.WhatsApp.LastRecipient);
        Assert.Equal(2, await harness.Db.NotificationDeliveryAttempts.CountAsync(a => a.DeliveryId == delivery.Id));
    }

    [Fact]
    public async Task Staff_phone_added_then_resend_sends_the_live_phone()
    {
        await using var harness = await CatalogHarness.CreateAsync();
        var user = new User(harness.Tenant.Id, "staff-auth", "Ops", "ops@club.test");
        user.SetPhone(LivePhone);
        harness.Db.Users.Add(user);
        var delivery = SeedFailedWhatsApp(harness, user.Id, NotificationRecipientKind.B2BUser, StalePhone, attemptCount: 1);
        AddWhatsAppTemplate(harness);
        await harness.Db.SaveChangesAsync();

        var resent = await harness.Notifications.ResendAsync(delivery.Id, CancellationToken.None);

        Assert.Equal(NotificationDeliveryStatus.Sent, resent.Status);
        Assert.Null(resent.ErrorMessage);
        Assert.Equal(LivePhone, harness.WhatsApp.LastRecipient);
        Assert.Equal(1, harness.WhatsApp.SendCount);
    }

    [Fact]
    public async Task Manual_resend_resets_the_retry_budget_and_backs_off()
    {
        await using var harness = await CatalogHarness.CreateAsync();
        var delivery = FailedEmail(harness.Tenant.Id, attemptCount: 5);
        for (var number = 1; number <= 5; number++)
        {
            delivery.AddAttempt(new NotificationDeliveryAttempt
            {
                DeliveryId = delivery.Id,
                AttemptNumber = number,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-number),
                Outcome = NotificationAttemptOutcome.TransientFailure,
            });
        }

        harness.Db.Notifications.Add(delivery.Notification);
        harness.Db.NotificationTemplates.Add(EmailTemplate(delivery.Notification.EventType));
        await harness.Db.SaveChangesAsync();

        var email = new ThrowingEmail();
        var processor = Processor(harness.Db, email);
        var before = DateTimeOffset.UtcNow;

        await processor.ResendAsync(delivery.Id, CancellationToken.None);

        var after = DateTimeOffset.UtcNow;
        var stored = await harness.Db.NotificationDeliveries
            .AsNoTracking()
            .Include(item => item.Attempts)
            .SingleAsync(item => item.Id == delivery.Id);
        Assert.Equal(1, email.Calls);
        Assert.Equal(NotificationDeliveryStatus.Queued, stored.Status);
        Assert.Equal(1, stored.AttemptCount);
        Assert.Equal(6, stored.Attempts.Count);
        Assert.Contains(stored.Attempts, attempt => attempt.AttemptNumber == 6);
        Assert.NotNull(stored.NextAttemptAt);
        Assert.InRange(
            stored.NextAttemptAt.Value,
            before.AddMinutes(1).AddSeconds(-1),
            after.AddMinutes(1).AddSeconds(1));
    }

    [Fact]
    public async Task List_hides_resend_for_channels_without_a_provider()
    {
        await using var harness = await CatalogHarness.CreateAsync();
        var sms = FailedDelivery(harness.Tenant.Id, NotificationChannel.Sms, NotificationDeliveryFailures.SmsUnavailable);
        var push = FailedDelivery(harness.Tenant.Id, NotificationChannel.Push, NotificationDeliveryFailures.NoExternalProvider);
        var unsupported = FailedDelivery(harness.Tenant.Id, NotificationChannel.WhatsApp, "Unsupported channel WhatsApp.");
        var missing = FailedDelivery(harness.Tenant.Id, NotificationChannel.WhatsApp, NotificationDeliveryFailures.WhatsAppMissing);
        missing.AddAttempt(new NotificationDeliveryAttempt
        {
            DeliveryId = missing.Id,
            AttemptNumber = 1,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            Outcome = NotificationAttemptOutcome.PermanentFailure,
            ErrorMessage = NotificationDeliveryFailures.WhatsAppMissing,
        });
        harness.Db.Notifications.AddRange(sms.Notification, push.Notification, unsupported.Notification, missing.Notification);
        await harness.Db.SaveChangesAsync();

        var listed = await harness.Notifications.ListAsync(
            new CatalogNotificationListQuery(null, null, null, null, null, null),
            CancellationToken.None);

        Assert.False(listed.Single(item => item.Id == sms.Id).CanResend);
        Assert.False(listed.Single(item => item.Id == push.Id).CanResend);
        Assert.False(listed.Single(item => item.Id == unsupported.Id).CanResend);
        var missingRow = listed.Single(item => item.Id == missing.Id);
        Assert.True(missingRow.CanResend);
        var attempt = Assert.Single(missingRow.Attempts);
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.Equal(NotificationChannel.WhatsApp, attempt.Channel);
        Assert.Equal(NotificationDeliveryFailures.WhatsAppMissing, attempt.ErrorMessage);
    }

    [Fact]
    public async Task Fresh_sending_row_is_not_reclaimed_and_the_provider_timeout_is_inside_the_lease()
    {
        Assert.True(NotificationOutboxProcessor.SendLease > NotificationOutboxProcessor.ProviderCallTimeout);

        var tenantId = Guid.NewGuid();
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider { TenantId = tenantId });
        var delivery = FailedEmail(tenantId, attemptCount: 0);
        delivery.Status = NotificationDeliveryStatus.Sending;
        delivery.Touch();
        db.Notifications.Add(delivery.Notification);
        db.NotificationTemplates.Add(EmailTemplate(delivery.Notification.EventType));
        await db.SaveChangesAsync();

        var email = new CountingEmail();
        var processor = Processor(db, email);
        await processor.ProcessDueAsync();
        await processor.ProcessDeliveryAsync(delivery.Id);

        Assert.Equal(0, email.Calls);
        var stored = await db.NotificationDeliveries.AsNoTracking().SingleAsync(item => item.Id == delivery.Id);
        Assert.Equal(NotificationDeliveryStatus.Sending, stored.Status);
    }

    [DockerFact]
    public async Task Overlapping_resend_does_not_claim_the_failure_the_winner_just_saved()
    {
        var factory = _postgres.Factory ?? throw new InvalidOperationException("PostgreSQL fixture was not initialized.");
        await AssertStaleResendDoesNotSendAsync(factory, versioned: true);
        await AssertStaleResendDoesNotSendAsync(factory, versioned: false);
    }

    [Fact]
    public async Task Overlapping_resends_and_sweep_send_once_in_memory()
    {
        var database = Guid.NewGuid().ToString("N");
        var tenant = new FakeTenantProvider { TenantId = Guid.NewGuid() };
        await AssertOneProviderSendAsync(() => InMemoryAppDb.Create(tenant, database), tenant);
    }

    [DockerFact]
    public async Task Overlapping_resends_and_sweep_send_once()
    {
        var factory = _postgres.Factory ?? throw new InvalidOperationException("PostgreSQL fixture was not initialized.");
        var tenant = new FakeTenantProvider { TenantId = Guid.NewGuid() };
        await AssertOneProviderSendAsync(() => factory.Create(tenant), tenant);
    }

    private static async Task AssertOneProviderSendAsync(Func<AppDbContext> create, FakeTenantProvider tenant)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Tenant id is required.");
        var email = new BlockingEmail();
        Guid deliveryId;
        await using (var seed = create())
        {
            var delivery = FailedEmail(tenantId, attemptCount: 2);
            delivery.AddAttempt(new NotificationDeliveryAttempt
            {
                DeliveryId = delivery.Id,
                AttemptNumber = 1,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                Outcome = NotificationAttemptOutcome.PermanentFailure,
            });
            seed.Notifications.Add(delivery.Notification);
            seed.NotificationTemplates.Add(EmailTemplate(delivery.Notification.EventType));
            await seed.SaveChangesAsync();
            deliveryId = delivery.Id;
        }

        await using var firstDb = create();
        await using var secondDb = create();
        await using var sweepDb = create();
        var first = Processor(firstDb, email);
        var second = Processor(secondDb, email);
        var sweep = Processor(sweepDb, email);

        var resend = Task.Run(() => first.ResendAsync(deliveryId));
        var entered = await Task.WhenAny(email.Entered, resend).WaitAsync(TimeSpan.FromSeconds(15));
        if (entered != email.Entered)
        {
            await resend;
        }

        Assert.Equal(1, email.Calls);
        var loser = Task.Run(async () =>
        {
            try
            {
                await second.ResendAsync(deliveryId);
            }
            catch (InvalidOperationException ex) when (ex.Message == "Only failed deliveries can be resent.")
            {
            }
        });
        await sweep.ProcessDueAsync();
        await sweep.ProcessDeliveryAsync(deliveryId);
        await loser;
        Assert.Equal(1, email.Calls);

        email.Release();
        await resend.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, email.Calls);

        await using var verify = create();
        var stored = await verify.NotificationDeliveries
            .AsNoTracking()
            .Include(item => item.Attempts)
            .SingleAsync(item => item.Id == deliveryId);
        Assert.Equal(NotificationDeliveryStatus.Sent, stored.Status);
        Assert.Equal(1, stored.AttemptCount);
        Assert.Equal(2, stored.Attempts.Count);
    }

    private static async Task AssertStaleResendDoesNotSendAsync(PostgresAppDbFactory factory, bool versioned)
    {
        var tenant = new FakeTenantProvider { TenantId = Guid.NewGuid() };
        var email = new PermanentFailureEmail();
        Guid deliveryId;
        await using (var seed = factory.Create(tenant))
        {
            var delivery = FailedEmail(tenant.TenantId!.Value, attemptCount: 1);
            delivery.Notification.EventType = versioned ? "notice.email.versioned" : "notice.email.plain";
            if (versioned)
            {
                delivery.Touch();
            }

            delivery.AddAttempt(new NotificationDeliveryAttempt
            {
                DeliveryId = delivery.Id,
                AttemptNumber = 1,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                Outcome = NotificationAttemptOutcome.PermanentFailure,
            });
            seed.Notifications.Add(delivery.Notification);
            seed.NotificationTemplates.Add(EmailTemplate(delivery.Notification.EventType));
            await seed.SaveChangesAsync();
            deliveryId = delivery.Id;
        }

        await using (var check = factory.Create(tenant))
        {
            var version = await check.NotificationDeliveries.AsNoTracking()
                .Where(item => item.Id == deliveryId)
                .Select(item => item.UpdatedAt)
                .SingleAsync();
            if (versioned)
            {
                Assert.NotNull(version);
            }
            else
            {
                Assert.Null(version);
            }
        }

        var hold = new HoldClaimUpdate();
        await using var winnerDb = factory.Create(tenant);
        await using var loserDb = factory.Create(tenant, hold);
        var winner = Processor(winnerDb, email);
        var loser = Processor(loserDb, email);
        var stale = Task.Run(() => loser.ResendAsync(deliveryId));
        await hold.Ready.WaitAsync(TimeSpan.FromSeconds(15));

        await winner.ResendAsync(deliveryId);
        Assert.Equal(1, email.Calls);
        hold.Release();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await stale);
        Assert.Equal("Only failed deliveries can be resent.", error.Message);
        Assert.Equal(1, email.Calls);

        await using var verify = factory.Create(tenant);
        var attempts = await verify.NotificationDeliveryAttempts.CountAsync(item => item.DeliveryId == deliveryId);
        Assert.Equal(2, attempts);
    }

    private static NotificationOutboxProcessor Processor(AppDbContext db, IEmailProvider email) =>
        new(db, email, new UnusedWhatsApp(), NullLogger<NotificationOutboxProcessor>.Instance, TimeProvider.System);

    private static Customer AddCustomer(CatalogHarness harness, string? phone)
    {
        var customer = new Customer
        {
            TenantId = harness.Tenant.Id,
            Name = "Ana",
            Email = "ana@club.test",
            Phone = phone,
        };
        harness.Db.Customers.Add(customer);
        return customer;
    }

    private static void AddWhatsAppTemplate(CatalogHarness harness) =>
        harness.Db.NotificationTemplates.Add(new NotificationTemplate
        {
            EventType = EventType,
            Channel = NotificationChannel.WhatsApp,
            BodyTemplate = "Pedido {{orderNumber}}",
            WhatsAppTemplateName = "catalog_order_status_update",
        });

    private static NotificationDelivery SeedFailedWhatsApp(
        CatalogHarness harness,
        Guid recipientId,
        NotificationRecipientKind kind,
        string? snapshotPhone,
        int attemptCount)
    {
        var notification = new Notification
        {
            TenantId = harness.Tenant.Id,
            EventType = EventType,
            AggregateType = "CatalogOrder",
            AggregateId = Guid.NewGuid(),
            Payload = new Dictionary<string, string?>(),
        };
        var delivery = new NotificationDelivery
        {
            TenantId = harness.Tenant.Id,
            Notification = notification,
            NotificationId = notification.Id,
            Channel = NotificationChannel.WhatsApp,
            RecipientKind = kind,
            RecipientId = recipientId,
            RecipientPhone = snapshotPhone,
            Status = NotificationDeliveryStatus.Failed,
            ErrorMessage = NotificationDeliveryFailures.WhatsAppMissing,
            AttemptCount = attemptCount,
        };
        delivery.AddAttempt(new NotificationDeliveryAttempt
        {
            DeliveryId = delivery.Id,
            AttemptNumber = 1,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            Outcome = NotificationAttemptOutcome.PermanentFailure,
        });
        notification.AddDelivery(delivery);
        harness.Db.Notifications.Add(notification);
        return delivery;
    }

    private static NotificationDelivery FailedEmail(Guid tenantId, int attemptCount)
    {
        var delivery = FailedDelivery(tenantId, NotificationChannel.Email, "temporary");
        delivery.RecipientEmail = "ana@club.test";
        delivery.AttemptCount = attemptCount;
        delivery.Notification.EventType = "notice.email";
        return delivery;
    }

    private static NotificationDelivery FailedDelivery(Guid tenantId, NotificationChannel channel, string error)
    {
        var notification = new Notification
        {
            TenantId = tenantId,
            EventType = EventType,
            AggregateType = "Notice",
            AggregateId = Guid.NewGuid(),
            Payload = new Dictionary<string, string?>(),
        };
        var delivery = new NotificationDelivery
        {
            TenantId = tenantId,
            Notification = notification,
            NotificationId = notification.Id,
            Channel = channel,
            RecipientKind = NotificationRecipientKind.Customer,
            Status = NotificationDeliveryStatus.Failed,
            ErrorMessage = error,
            AttemptCount = 1,
        };
        notification.AddDelivery(delivery);
        return delivery;
    }

    private static NotificationTemplate EmailTemplate(string eventType) => new()
    {
        EventType = eventType,
        Channel = NotificationChannel.Email,
        BodyTemplate = "Hello",
        SubjectTemplate = "Hello",
    };

    private sealed class CountingEmail : IEmailProvider
    {
        public int Calls { get; private set; }

        public Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class PermanentFailureEmail : IEmailProvider
    {
        public int Calls { get; private set; }

        public Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("provider rejected");
        }
    }

    private sealed class HoldClaimUpdate : DbCommandInterceptor
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int seen;

        public Task Ready => ready.Task;

        public void Release() => release.TrySetResult();

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("notification_deliveries", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Increment(ref seen) == 1)
            {
                ready.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class ThrowingEmail : IEmailProvider
    {
        public int Calls { get; private set; }

        public Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new HttpRequestException("temporary");
        }
    }

    private sealed class BlockingEmail : IEmailProvider
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public Task Entered => entered.Task;

        public void Release() => release.TrySetResult();

        public async Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
        }
    }

    private sealed class UnusedWhatsApp : IWhatsAppProvider
    {
        public Task SendAsync(string recipient, string body, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected WhatsApp call.");

        public Task<string?> SendTemplateAsync(
            string recipient,
            string templateName,
            string languageCode,
            IReadOnlyList<string> bodyParameters,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected WhatsApp call.");
    }
}
