using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Authorization;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Modules.Notifications.Dtos;
using Platform.Api.Modules.Notifications.Services;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Notifications;
using Platform.Api.Notifications.Providers.Dev;
using Platform.Api.Tests.Catalog;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Rentals;

public sealed class RentalsEmailNotificationTests
{
    [Fact]
    public async Task New_tenant_reserve_and_cancel_enqueues_email_and_not_whatsapp()
    {
        await using var ctx = await CreateContextAsync(requiresDeposit: false);

        var created = await ctx.Reservations.CreateReservationAsync(
            ctx.Harness.CustomerId,
            CreateRequest(ctx.Harness),
            CancellationToken.None);
        Assert.Equal(ReservationStatus.Confirmed, created.Status);

        var canceled = await ctx.Reservations.CancelAsync(created.Id, CancellationToken.None);
        Assert.Equal(ReservationStatus.Canceled, canceled.Status);

        var notifications = ctx.Harness.Db.Notifications.ToList();
        var deliveries = ctx.Harness.Db.NotificationDeliveries.ToList();
        var confirmedEmail = EmailDelivery(notifications, deliveries, RentalEventTypes.ReservationConfirmed);
        var canceledEmail = EmailDelivery(notifications, deliveries, RentalEventTypes.ReservationCanceled);

        Assert.Equal(NotificationDeliveryStatus.Queued, confirmedEmail.Status);
        Assert.Equal(NotificationDeliveryStatus.Queued, canceledEmail.Status);
        Assert.Equal(ctx.Harness.TenantId, confirmedEmail.TenantId);
        Assert.Equal(ctx.Harness.TenantId, canceledEmail.TenantId);
        Assert.Equal("cliente@club.test", confirmedEmail.RecipientEmail);
        Assert.Equal("cliente@club.test", canceledEmail.RecipientEmail);
        Assert.Contains(ctx.Scheduler.Scheduled, id => id == confirmedEmail.Id);
        Assert.Contains(ctx.Scheduler.Scheduled, id => id == canceledEmail.Id);
        Assert.DoesNotContain(deliveries, delivery => delivery.Channel == NotificationChannel.WhatsApp);
        Assert.DoesNotContain(deliveries, delivery => delivery.Channel == NotificationChannel.Sms);

        var configs = ctx.Harness.Db.TenantNotificationChannelConfigs.ToList();
        Assert.Equal(RentalEventTypes.Notifying.Length * 4, configs.Count);
        Assert.All(
            configs.Where(config => config.Channel is NotificationChannel.InApp or NotificationChannel.Email),
            config => Assert.True(config.IsActive));
        Assert.All(
            configs.Where(config => config.Channel is NotificationChannel.WhatsApp or NotificationChannel.Sms),
            config => Assert.False(config.IsActive));
    }

    [Fact]
    public async Task Existing_inactive_email_config_is_not_overwritten_on_reserve_or_cancel()
    {
        await using var ctx = await CreateContextAsync(requiresDeposit: false);
        await ctx.Publisher.EnsureReadyAsync(CancellationToken.None);
        var confirmedEmail = await ctx.Harness.Db.TenantNotificationChannelConfigs.SingleAsync(config =>
            config.EventType == RentalEventTypes.ReservationConfirmed
            && config.Channel == NotificationChannel.Email);
        confirmedEmail.IsActive = false;
        await ctx.Harness.Db.SaveChangesAsync();

        var created = await ctx.Reservations.CreateReservationAsync(
            ctx.Harness.CustomerId,
            CreateRequest(ctx.Harness),
            CancellationToken.None);
        await ctx.Reservations.CancelAsync(created.Id, CancellationToken.None);

        var notifications = ctx.Harness.Db.Notifications.ToList();
        var deliveries = ctx.Harness.Db.NotificationDeliveries.ToList();
        Assert.DoesNotContain(
            notifications,
            notification => notification.EventType == RentalEventTypes.ReservationConfirmed
                && deliveries.Any(delivery =>
                    delivery.NotificationId == notification.Id
                    && delivery.Channel == NotificationChannel.Email));
        Assert.NotNull(EmailDelivery(notifications, deliveries, RentalEventTypes.ReservationCanceled));
        Assert.DoesNotContain(deliveries, delivery => delivery.Channel == NotificationChannel.WhatsApp);

        var stored = await ctx.Harness.Db.TenantNotificationChannelConfigs.SingleAsync(config =>
            config.EventType == RentalEventTypes.ReservationConfirmed
            && config.Channel == NotificationChannel.Email);
        Assert.False(stored.IsActive);
        Assert.Equal(confirmedEmail.Id, stored.Id);
    }

    [Fact]
    public async Task Reserve_and_cancel_does_not_enqueue_or_rewrite_another_tenants_email()
    {
        await using var ctx = await CreateContextAsync(requiresDeposit: false);
        var other = new Tenant("Outro Clube", "88888888000191", subdomain: "outro-clube");
        ctx.Harness.Db.Tenants.Add(other);
        foreach (var eventType in RentalEventTypes.Notifying)
        {
            foreach (var channel in new[]
                     {
                         NotificationChannel.InApp,
                         NotificationChannel.Email,
                         NotificationChannel.WhatsApp,
                         NotificationChannel.Sms,
                     })
            {
                ctx.Harness.Db.TenantNotificationChannelConfigs.Add(new TenantNotificationChannelConfig
                {
                    TenantId = other.Id,
                    EventType = eventType,
                    Channel = channel,
                    IsActive = channel == NotificationChannel.InApp,
                });
            }
        }

        await ctx.Harness.Db.SaveChangesAsync();

        await using var before = InMemoryAppDb.Create(
            new FakeTenantProvider { TenantId = null },
            ctx.Harness.DatabaseName);
        var otherBefore = await before.TenantNotificationChannelConfigs
            .Where(config => config.TenantId == other.Id)
            .Select(config => new { config.Id, config.EventType, config.Channel, config.IsActive })
            .ToListAsync();

        var created = await ctx.Reservations.CreateReservationAsync(
            ctx.Harness.CustomerId,
            CreateRequest(ctx.Harness),
            CancellationToken.None);
        await ctx.Reservations.CancelAsync(created.Id, CancellationToken.None);

        await using var after = InMemoryAppDb.Create(
            new FakeTenantProvider { TenantId = null },
            ctx.Harness.DatabaseName);
        var deliveries = await after.NotificationDeliveries
            .Include(delivery => delivery.Notification)
            .ToListAsync();
        Assert.NotEmpty(deliveries);
        Assert.All(deliveries, delivery => Assert.Equal(ctx.Harness.TenantId, delivery.TenantId));
        Assert.Contains(
            deliveries,
            delivery => delivery.Channel == NotificationChannel.Email
                && delivery.Notification.EventType == RentalEventTypes.ReservationConfirmed);
        Assert.Contains(
            deliveries,
            delivery => delivery.Channel == NotificationChannel.Email
                && delivery.Notification.EventType == RentalEventTypes.ReservationCanceled);
        Assert.DoesNotContain(deliveries, delivery => delivery.Channel == NotificationChannel.WhatsApp);
        Assert.DoesNotContain(deliveries, delivery => delivery.TenantId == other.Id);

        var otherAfter = await after.TenantNotificationChannelConfigs
            .Where(config => config.TenantId == other.Id)
            .ToListAsync();
        Assert.Equal(otherBefore.Count, otherAfter.Count);
        Assert.All(otherBefore, previous =>
        {
            var current = Assert.Single(otherAfter, config => config.Id == previous.Id);
            Assert.Equal(previous.EventType, current.EventType);
            Assert.Equal(previous.Channel, current.Channel);
            Assert.Equal(previous.IsActive, current.IsActive);
        });
        Assert.All(
            otherAfter.Where(config => config.Channel == NotificationChannel.Email),
            config => Assert.False(config.IsActive));
    }

    [Fact]
    public async Task Enabled_rentals_email_stays_on_dev_provider_when_allow_external_email_is_unset()
    {
        await AssertEnabledEmailUsesDevProvider(
            allowExternalEmail: null,
            allowExternalDelivery: null);
    }

    [Fact]
    public async Task Enabled_rentals_email_stays_on_dev_provider_when_allow_external_email_is_false()
    {
        await AssertEnabledEmailUsesDevProvider(
            allowExternalEmail: false,
            allowExternalDelivery: true);
    }

    private static async Task AssertEnabledEmailUsesDevProvider(
        bool? allowExternalEmail,
        bool? allowExternalDelivery)
    {
        await using var gate = EmailGate.Create(allowExternalEmail, allowExternalDelivery);
        Assert.IsType<DevEmailProvider>(gate.Email);

        await using var ctx = await CreateContextAsync(requiresDeposit: false, gate.Email);
        ctx.Harness.Db.TenantModules.Add(
            new TenantModule(ctx.Harness.TenantId, PlatformModules.Rentals, isActive: true));
        await ctx.Harness.Db.SaveChangesAsync();

        var configs = CreateConfigService(ctx.Harness);
        var enabled = await configs.UpsertAsync(
            new UpsertNotificationChannelConfigRequest
            {
                EventType = RentalEventTypes.ReservationConfirmed,
                Channel = NotificationChannel.Email,
                IsActive = true,
            },
            CancellationToken.None);
        Assert.True(enabled.IsActive);

        var saved = Assert.Single(
            ctx.Harness.Db.TenantNotificationChannelConfigs.IgnoreQueryFilters(),
            item => item.Channel == NotificationChannel.Email
                && item.EventType == RentalEventTypes.ReservationConfirmed
                && item.IsActive);
        Assert.Equal(ctx.Harness.TenantId, saved.TenantId);

        await ctx.Reservations.CreateReservationAsync(
            ctx.Harness.CustomerId,
            CreateRequest(ctx.Harness),
            CancellationToken.None);

        foreach (var id in ctx.Scheduler.Scheduled)
        {
            await ctx.Processor.ProcessDeliveryAsync(id, CancellationToken.None);
        }

        var email = Assert.Single(
            ctx.Harness.Db.NotificationDeliveries,
            delivery => delivery.Channel == NotificationChannel.Email);
        Assert.Equal(NotificationDeliveryStatus.Sent, email.Status);
        Assert.Equal(ctx.Harness.TenantId, email.TenantId);
        Assert.Equal("cliente@club.test", email.RecipientEmail);
        Assert.DoesNotContain(
            ctx.Harness.Db.NotificationDeliveries,
            delivery => delivery.Channel == NotificationChannel.WhatsApp);
        Assert.Contains(
            gate.Logs.Entries,
            log => log.Level == LogLevel.Information
                && log.Message.Contains("DEV EMAIL", StringComparison.Ordinal)
                && log.Message.Contains("cliente@club.test", StringComparison.Ordinal)
                && log.Message.Contains("Reserva Quadra 1 confirmada", StringComparison.Ordinal));
        Assert.DoesNotContain(
            gate.Logs.Entries,
            log => log.Message.Contains("re_test_not_a_real_key", StringComparison.Ordinal)
                || log.Message.Contains("Cliente B2C", StringComparison.Ordinal)
                || log.Message.Contains("Clube Reserva", StringComparison.Ordinal)
                || log.Message.Contains("<html", StringComparison.OrdinalIgnoreCase)
                || log.Message.Contains("agenda?reservation", StringComparison.Ordinal));
    }

    private static NotificationChannelConfigService CreateConfigService(LocationBookingHarness harness)
    {
        var permissions = new FakePermissionResolver();
        return new NotificationChannelConfigService(
            harness.Db,
            harness.TenantProvider,
            new TenantModuleAccessor(harness.Db, harness.TenantProvider),
            new CatalogNotificationPublisher(
                harness.Db,
                harness.TenantProvider,
                permissions,
                NullLogger<CatalogNotificationPublisher>.Instance),
            new RentalsNotificationPublisher(
                harness.Db,
                harness.TenantProvider,
                NullLogger<RentalsNotificationPublisher>.Instance));
    }

    private static async Task<Context> CreateContextAsync(
        bool requiresDeposit,
        IEmailProvider? emailProvider = null)
    {
        var harness = await LocationBookingHarness.CreateAsync();
        var rental = await harness.Db.RentalAssets.FirstAsync(r => r.Id == harness.RentalAssetId);
        rental.RequiresDeposit = requiresDeposit;
        harness.Db.RentalPricings.Add(new RentalPricing
        {
            TenantId = harness.TenantId,
            RentalAssetId = harness.RentalAssetId,
            DayOfWeek = LocationBookingHarness.Date.DayOfWeek,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(22, 0),
            PricePerHour = 100m,
            RequiresDeposit = requiresDeposit,
            DepositPercentage = requiresDeposit ? 50m : 0m,
        });
        await harness.Db.SaveChangesAsync();

        var publisher = new RentalsNotificationPublisher(
            harness.Db,
            harness.TenantProvider,
            NullLogger<RentalsNotificationPublisher>.Instance);
        var scheduler = new RecordingOutboxScheduler();
        var email = emailProvider ?? new DevEmailRecorder();
        var processor = new NotificationOutboxProcessor(
            harness.Db,
            email,
            new DevWhatsAppRecorder(),
            NullLogger<NotificationOutboxProcessor>.Instance,
            TimeProvider.System);
        var reservations = new ReservationService(
            harness.Db,
            harness.TenantProvider,
            new FakeTrialGuard(),
            TestReservationQueue.Create(harness.Db, harness.TenantProvider),
            publisher.AsDomainEvents(),
            scheduler,
            LocationBookingHarness.BookingClock);

        return new Context(harness, publisher, scheduler, processor, email, reservations);
    }

    private static CreateReservationRequestDto CreateRequest(LocationBookingHarness harness) =>
        new()
        {
            UnitId = harness.UnitId,
            Date = LocationBookingHarness.Date,
            StartTime = LocationBookingHarness.Start,
            EndTime = LocationBookingHarness.End,
            Items = [new CreateReservationItemRequestDto { AssetId = harness.AssetId, Quantity = 1 }],
        };

    private static NotificationDelivery EmailDelivery(
        IReadOnlyList<Notification> notifications,
        IReadOnlyList<NotificationDelivery> deliveries,
        string eventType)
    {
        var notification = Assert.Single(notifications, item => item.EventType == eventType);
        return Assert.Single(
            deliveries,
            delivery => delivery.NotificationId == notification.Id
                && delivery.Channel == NotificationChannel.Email);
    }

    private sealed record Context(
        LocationBookingHarness Harness,
        RentalsNotificationPublisher Publisher,
        RecordingOutboxScheduler Scheduler,
        NotificationOutboxProcessor Processor,
        IEmailProvider Email,
        ReservationService Reservations) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Harness.DisposeAsync();
    }

    private sealed class EmailGate : IAsyncDisposable
    {
        private EmailGate(ServiceProvider provider, IEmailProvider email, CapturingLoggerProvider logs)
        {
            Provider = provider;
            Email = email;
            Logs = logs;
        }

        public ServiceProvider Provider { get; }

        public IEmailProvider Email { get; }

        public CapturingLoggerProvider Logs { get; }

        public static EmailGate Create(bool? allowExternalEmail, bool? allowExternalDelivery)
        {
            var values = new Dictionary<string, string?>
            {
                ["Resend:ApiKey"] = "re_test_not_a_real_key",
                ["Resend:FromEmail"] = "dev@rolvix.test",
                ["WhatsApp:AccessToken"] = "test-access-token",
                ["WhatsApp:PhoneNumberId"] = "000000000000000",
            };
            if (allowExternalDelivery is { } global)
            {
                values["Notifications:AllowExternalDelivery"] = global ? "true" : "false";
            }

            if (allowExternalEmail is { } email)
            {
                values["Notifications:AllowExternalEmail"] = email ? "true" : "false";
            }

            var logs = new CapturingLoggerProvider();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddProvider(logs));
            services.AddNotificationInfrastructure(configuration, new FakeHostEnvironment("Production"));
            var provider = services.BuildServiceProvider();
            return new EmailGate(provider, provider.GetRequiredService<IEmailProvider>(), logs);
        }

        public ValueTask DisposeAsync() => Provider.DisposeAsync();

        private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
        {
            public string EnvironmentName { get; set; } = environmentName;
            public string ApplicationName { get; set; } = "Platform.Api.Tests";
            public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
            public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        }
    }
}
