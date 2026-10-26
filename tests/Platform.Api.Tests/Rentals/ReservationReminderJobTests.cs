using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Jobs;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Notifications;
using Platform.Api.Tests.Catalog;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.Rentals;

public sealed class ReservationReminderJobTests
{
    [Theory]
    [InlineData(ReservationStatus.Confirmed, 1, false)]
    [InlineData(ReservationStatus.Confirmed, 0, true)]
    [InlineData(ReservationStatus.Confirmed, -1, true)]
    [InlineData(ReservationStatus.PendingDeposit, 1, false)]
    [InlineData(ReservationStatus.PendingDeposit, 0, true)]
    public async Task Reminder_requires_booking_at_least_24_hours_before_start(
        ReservationStatus status, long ticksAfterCutoff, bool shouldSend)
    {
        await using var ctx = await CreateContextAsync();
        await EnableReminderWhatsAppAsync(ctx);
        await EnableReminderEmailAsync(ctx);
        var now = DateTimeOffset.Parse("2026-09-08T12:00:00Z");
        var start = now.AddHours(12);
        await SeedAsync(ctx, status, start);
        var reservation = await ctx.Harness.Db.Reservations.SingleAsync();
        ctx.Harness.Db.Entry(reservation).Property(r => r.CreatedAt).CurrentValue =
            start.AddHours(-24).AddTicks(ticksAfterCutoff);
        await ctx.Harness.Db.SaveChangesAsync();

        await CreateJob(ctx, now).ExecuteAsync(CancellationToken.None);
        await ProcessQueuedAsync(ctx);

        Assert.Equal(shouldSend ? 1 : 0, ctx.WhatsApp.SendCount);
        Assert.Equal(shouldSend ? 1 : 0, ctx.Email.SendCount);
        if (!shouldSend)
        {
            Assert.Empty(ctx.Scheduler.Scheduled);
        }
    }

    [Fact]
    public void Lead_window_is_24_hours_before_start()
    {
        var start = DateTimeOffset.Parse("2026-09-10T13:00:00Z");
        Assert.False(ReservationReminderSchedule.IsInLeadWindow(start, start.AddHours(-25)));
        Assert.True(ReservationReminderSchedule.IsInLeadWindow(start, start.AddHours(-24)));
        Assert.True(ReservationReminderSchedule.IsInLeadWindow(start, start.AddHours(-1)));
        Assert.False(ReservationReminderSchedule.IsInLeadWindow(start, start));
        Assert.False(ReservationReminderSchedule.IsInLeadWindow(start, start.AddHours(1)));
        Assert.Equal(TimeSpan.FromHours(24), ReservationReminderSchedule.LeadTime);
    }

    [Fact]
    public async Task Confirmed_reservation_in_24h_window_sends_reminder_once()
    {
        await using var ctx = await CreateContextAsync();
        await EnableReminderWhatsAppAsync(ctx);
        var now = DateTimeOffset.Parse("2026-09-08T12:00:00Z");
        var start = now.AddHours(12);
        await SeedAsync(ctx, ReservationStatus.Confirmed, start);

        var job = CreateJob(ctx, now);
        await job.ExecuteAsync(CancellationToken.None);
        await job.ExecuteAsync(CancellationToken.None);
        await ProcessQueuedAsync(ctx);

        Assert.Equal(1, ctx.WhatsApp.SendCount);
        Assert.Equal("rental_reservation_reminder", ctx.WhatsApp.LastTemplateName);
        Assert.Equal("pt_BR", ctx.WhatsApp.LastLanguage);
        Assert.Equal(4, ctx.WhatsApp.LastParameters.Count);
        Assert.Equal("Clube Reserva", ctx.WhatsApp.LastParameters[0]);
        Assert.Equal("Existing", ctx.WhatsApp.LastParameters[1]);
        Assert.Equal("Quadra 1", ctx.WhatsApp.LastParameters[2]);
        Assert.Equal(BrazilTimeZone.FormatCivilDateTime(start), ctx.WhatsApp.LastParameters[3]);
        Assert.DoesNotContain("-03:00", ctx.WhatsApp.LastParameters[3], StringComparison.Ordinal);
        Assert.DoesNotContain("Z", ctx.WhatsApp.LastParameters[3], StringComparison.Ordinal);
        Assert.Equal(1, ctx.Harness.Db.Notifications.Count(n => n.EventType == RentalEventTypes.ReservationReminder));
    }

    [Fact]
    public async Task Pending_deposit_in_window_sends_reminder()
    {
        await using var ctx = await CreateContextAsync();
        await EnableReminderWhatsAppAsync(ctx);
        var now = DateTimeOffset.Parse("2026-09-08T12:00:00Z");
        await SeedAsync(ctx, ReservationStatus.PendingDeposit, now.AddHours(20));

        await CreateJob(ctx, now).ExecuteAsync(CancellationToken.None);
        await ProcessQueuedAsync(ctx);

        Assert.Equal(1, ctx.WhatsApp.SendCount);
        Assert.Equal("rental_reservation_reminder", ctx.WhatsApp.LastTemplateName);
    }

    [Fact]
    public async Task Outside_24h_window_does_not_remind()
    {
        await using var ctx = await CreateContextAsync();
        await EnableReminderWhatsAppAsync(ctx);
        var now = DateTimeOffset.Parse("2026-09-08T12:00:00Z");
        await SeedAsync(ctx, ReservationStatus.Confirmed, now.AddHours(25));

        await CreateJob(ctx, now).ExecuteAsync(CancellationToken.None);
        await ProcessQueuedAsync(ctx);

        Assert.Equal(0, ctx.WhatsApp.SendCount);
        Assert.Empty(ctx.Scheduler.Scheduled);
    }

    [Fact]
    public async Task After_start_does_not_remind()
    {
        await using var ctx = await CreateContextAsync();
        await EnableReminderWhatsAppAsync(ctx);
        var now = DateTimeOffset.Parse("2026-09-08T12:00:00Z");
        await SeedAsync(ctx, ReservationStatus.Confirmed, now.AddHours(-1));

        await CreateJob(ctx, now).ExecuteAsync(CancellationToken.None);
        await ProcessQueuedAsync(ctx);

        Assert.Equal(0, ctx.WhatsApp.SendCount);
    }

    [Theory]
    [InlineData(ReservationStatus.Canceled)]
    [InlineData(ReservationStatus.Completed)]
    public async Task Terminal_status_does_not_remind(ReservationStatus status)
    {
        await using var ctx = await CreateContextAsync();
        await EnableReminderWhatsAppAsync(ctx);
        var now = DateTimeOffset.Parse("2026-09-08T12:00:00Z");
        await SeedAsync(ctx, status, now.AddHours(6));

        await CreateJob(ctx, now).ExecuteAsync(CancellationToken.None);
        await ProcessQueuedAsync(ctx);

        Assert.Equal(0, ctx.WhatsApp.SendCount);
        Assert.Empty(ctx.Harness.Db.Notifications.Where(n => n.EventType == RentalEventTypes.ReservationReminder));
    }

    [Fact]
    public async Task Email_reminder_on_and_whatsapp_off_publishes_email_only()
    {
        await using var ctx = await CreateContextAsync();
        await EnableReminderEmailAsync(ctx);
        var now = DateTimeOffset.Parse("2026-09-08T12:00:00Z");
        await SeedAsync(ctx, ReservationStatus.Confirmed, now.AddHours(6));

        await CreateJob(ctx, now).ExecuteAsync(CancellationToken.None);
        await ProcessQueuedAsync(ctx);

        Assert.Equal(0, ctx.WhatsApp.SendCount);
        Assert.DoesNotContain(
            ctx.Harness.Db.NotificationDeliveries,
            d => d.Channel == NotificationChannel.WhatsApp);
        var email = Assert.Single(
            ctx.Harness.Db.NotificationDeliveries,
            d => d.Channel == NotificationChannel.Email);
        Assert.Equal(NotificationDeliveryStatus.Sent, email.Status);
        Assert.Equal(ctx.Harness.TenantId, email.TenantId);
        Assert.Equal("cliente@club.test", email.RecipientEmail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Active_rentals_module_queues_reminder_email_only_when_the_email_channel_is_on(
        bool emailOn)
    {
        await using var ctx = await CreateContextAsync();
        // The other external channel stays on in the negative case so the sweep still
        // reaches this tenant. Email absence is then the channel flag, not an inactive module.
        if (emailOn)
        {
            await EnableReminderEmailAsync(ctx);
            await SetReminderChannelAsync(ctx.Harness.Db, NotificationChannel.WhatsApp, isActive: false);
        }
        else
        {
            await EnableReminderWhatsAppAsync(ctx);
        }

        await SetReminderChannelAsync(ctx.Harness.Db, NotificationChannel.InApp, isActive: false);
        var now = DateTimeOffset.Parse("2026-09-08T12:00:00Z");
        await SeedAsync(ctx, ReservationStatus.Confirmed, now.AddHours(6));

        await CreateJob(ctx, now).ExecuteAsync(CancellationToken.None);
        await ProcessQueuedAsync(ctx);

        var deliveries = ctx.Harness.Db.NotificationDeliveries.ToList();
        if (emailOn)
        {
            var email = Assert.Single(deliveries);
            Assert.Equal(NotificationChannel.Email, email.Channel);
            Assert.Equal(NotificationDeliveryStatus.Sent, email.Status);
            Assert.Equal(ctx.Harness.TenantId, email.TenantId);
            Assert.Equal(1, ctx.Email.SendCount);
            Assert.Equal(0, ctx.WhatsApp.SendCount);
            return;
        }

        Assert.DoesNotContain(deliveries, delivery => delivery.Channel == NotificationChannel.Email);
        var whatsApp = Assert.Single(deliveries);
        Assert.Equal(NotificationChannel.WhatsApp, whatsApp.Channel);
        Assert.Equal(NotificationDeliveryStatus.Sent, whatsApp.Status);
        Assert.Equal(0, ctx.Email.SendCount);
        Assert.Equal(1, ctx.WhatsApp.SendCount);
    }

    [Fact]
    public async Task Two_tenants_in_one_sweep_each_get_one_whatsapp_delivery()
    {
        await using var ctx = await CreateContextAsync();
        await EnableReminderWhatsAppAsync(ctx);
        var now = DateTimeOffset.Parse("2026-09-08T12:00:00Z");
        await SeedAsync(ctx, ReservationStatus.Confirmed, now.AddHours(6));
        await SeedSecondTenantReminderAsync(ctx, now.AddHours(8));

        await CreateJob(ctx, now).ExecuteAsync(CancellationToken.None);
        await ProcessQueuedAsync(ctx);

        Assert.Equal(2, ctx.WhatsApp.SendCount);

        await using var unfiltered = InMemoryAppDb.Create(
            new FakeTenantProvider { TenantId = null },
            ctx.Harness.DatabaseName);
        var whatsApp = await unfiltered.NotificationDeliveries
            .Where(d => d.Channel == NotificationChannel.WhatsApp)
            .ToListAsync();
        Assert.Equal(2, whatsApp.Count);
        Assert.Equal(2, whatsApp.Select(d => d.TenantId).Distinct().Count());
        Assert.All(whatsApp.GroupBy(d => d.TenantId), g => Assert.Single(g));
        Assert.Equal(
            2,
            unfiltered.Notifications.Count(n => n.EventType == RentalEventTypes.ReservationReminder));
    }

    [Fact]
    public async Task Tenant_sweep_is_platform_scope_and_per_tenant_work_is_not()
    {
        await using var ctx = await CreateContextAsync();
        await EnableReminderWhatsAppAsync(ctx);
        var now = DateTimeOffset.Parse("2026-09-08T12:00:00Z");
        await SeedAsync(ctx, ReservationStatus.Confirmed, now.AddHours(6));
        var secondTenantId = await SeedSecondTenantReminderAsync(ctx, now.AddHours(8));
        var reads = new List<(Guid? TenantId, string? PlatformScopeReason)>();
        ctx.Clock.SetUtcNow(now);
        var job = new ReservationReminderJob(
            new ReminderJobTestScopeFactory(ctx.Harness.DatabaseName, ctx.Scheduler, reads),
            ctx.Clock,
            NullLogger<ReservationReminderJob>.Instance);

        // Two runs: the second must not inherit the first run's scope.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            reads.Clear();
            await job.ExecuteAsync(CancellationToken.None);

            Assert.False(PlatformScope.IsActive);
            Assert.Contains(reads, read => read.TenantId is null);
            Assert.All(
                reads.Where(read => read.TenantId is null),
                read => Assert.Equal("reservation-reminder-tenant-sweep", read.PlatformScopeReason));
            Assert.Equal(
                new HashSet<Guid?> { ctx.Harness.TenantId, secondTenantId },
                reads.Where(read => read.TenantId is not null).Select(read => read.TenantId).ToHashSet());
            Assert.All(
                reads.Where(read => read.TenantId is not null),
                read => Assert.Null(read.PlatformScopeReason));
        }
    }

    [Fact]
    public async Task Two_tenants_email_only_and_whatsapp_only_each_get_one_delivery()
    {
        await using var ctx = await CreateContextAsync();
        await EnableReminderEmailAsync(ctx);
        await SetReminderChannelAsync(ctx.Harness.Db, NotificationChannel.InApp, isActive: false);
        var now = DateTimeOffset.Parse("2026-09-08T12:00:00Z");
        await SeedAsync(ctx, ReservationStatus.Confirmed, now.AddHours(6));
        var tenantB = await SeedSecondTenantReminderAsync(
            ctx,
            now.AddHours(8),
            NotificationChannel.WhatsApp,
            inAppActive: false);

        await CreateJob(ctx, now).ExecuteAsync(CancellationToken.None);
        await ProcessQueuedAsync(ctx);

        Assert.Equal(1, ctx.WhatsApp.SendCount);

        await using var unfiltered = InMemoryAppDb.Create(
            new FakeTenantProvider { TenantId = null },
            ctx.Harness.DatabaseName);
        var deliveries = await unfiltered.NotificationDeliveries.ToListAsync();
        Assert.Equal(2, deliveries.Count);

        var email = Assert.Single(deliveries, d => d.TenantId == ctx.Harness.TenantId);
        Assert.Equal(NotificationChannel.Email, email.Channel);
        Assert.Equal(NotificationDeliveryStatus.Sent, email.Status);

        var whatsApp = Assert.Single(deliveries, d => d.TenantId == tenantB);
        Assert.Equal(NotificationChannel.WhatsApp, whatsApp.Channel);
        Assert.Equal(NotificationDeliveryStatus.Sent, whatsApp.Status);
    }

    [Fact]
    public async Task Inactive_rentals_module_skips_reminder_while_active_tenant_still_receives_one()
    {
        await using var ctx = await CreateContextAsync();
        await EnableReminderWhatsAppAsync(ctx);
        await SetReminderChannelAsync(ctx.Harness.Db, NotificationChannel.InApp, isActive: false);
        var now = DateTimeOffset.Parse("2026-09-08T12:00:00Z");
        await SeedAsync(ctx, ReservationStatus.Confirmed, now.AddHours(6));
        await SetRentalsModuleActiveAsync(ctx.Harness.Db, ctx.Harness.TenantId, isActive: false);
        var tenantB = await SeedSecondTenantReminderAsync(
            ctx,
            now.AddHours(8),
            NotificationChannel.WhatsApp,
            inAppActive: false);

        await CreateJob(ctx, now).ExecuteAsync(CancellationToken.None);
        await ProcessQueuedAsync(ctx);

        await using var unfiltered = InMemoryAppDb.Create(
            new FakeTenantProvider { TenantId = null },
            ctx.Harness.DatabaseName);
        var deliveries = await unfiltered.NotificationDeliveries.ToListAsync();
        Assert.DoesNotContain(deliveries, d => d.TenantId == ctx.Harness.TenantId);
        var activeDelivery = Assert.Single(deliveries);
        Assert.Equal(tenantB, activeDelivery.TenantId);
        Assert.Equal(NotificationChannel.WhatsApp, activeDelivery.Channel);
        Assert.Equal(1, ctx.WhatsApp.SendCount);

        await SetRentalsModuleActiveAsync(ctx.Harness.Db, ctx.Harness.TenantId, isActive: true);
        await CreateJob(ctx, now).ExecuteAsync(CancellationToken.None);
        await ProcessQueuedAsync(ctx);

        await using var after = InMemoryAppDb.Create(
            new FakeTenantProvider { TenantId = null },
            ctx.Harness.DatabaseName);
        var restored = await after.NotificationDeliveries.ToListAsync();
        Assert.Equal(2, restored.Count);
        var restoredForA = Assert.Single(restored, d => d.TenantId == ctx.Harness.TenantId);
        Assert.Equal(NotificationChannel.WhatsApp, restoredForA.Channel);
        Assert.Equal(NotificationDeliveryStatus.Sent, restoredForA.Status);
        var stillOneForB = Assert.Single(restored, d => d.TenantId == tenantB);
        Assert.Equal(NotificationChannel.WhatsApp, stillOneForB.Channel);
        Assert.Equal(2, ctx.WhatsApp.SendCount);
    }

    [Fact]
    public void Send_gate_allows_only_pending_or_confirmed_reservations_that_have_not_started()
    {
        var start = DateTimeOffset.Parse("2026-10-15T19:00:00Z");
        var bookedAt = DateTimeOffset.Parse("2026-10-14T23:49:17Z");

        Assert.True(ReservationReminderSendGate.ShouldSend(
            ReservationStatus.Confirmed,
            start,
            bookedAt));
        Assert.True(ReservationReminderSendGate.ShouldSend(
            ReservationStatus.PendingDeposit,
            start,
            bookedAt));
        Assert.False(ReservationReminderSendGate.ShouldSend(
            ReservationStatus.Canceled,
            start,
            bookedAt));
        Assert.False(ReservationReminderSendGate.ShouldSend(
            ReservationStatus.Completed,
            start,
            bookedAt));
        Assert.False(ReservationReminderSendGate.ShouldSend(
            ReservationStatus.Confirmed,
            start,
            start));
        Assert.False(ReservationReminderSendGate.ShouldSend(
            ReservationStatus.Confirmed,
            start,
            start.AddSeconds(1)));
    }

    [Theory]
    [InlineData(ReservationStatus.Confirmed, ReservationStatus.Canceled, NotificationChannel.Email)]
    [InlineData(ReservationStatus.Confirmed, ReservationStatus.Canceled, NotificationChannel.WhatsApp)]
    [InlineData(ReservationStatus.PendingDeposit, ReservationStatus.Canceled, NotificationChannel.Email)]
    [InlineData(ReservationStatus.Confirmed, ReservationStatus.Completed, NotificationChannel.Email)]
    public async Task Reminder_queued_while_active_is_not_sent_after_status_changes(
        ReservationStatus bookedStatus,
        ReservationStatus statusAtSend,
        NotificationChannel channel)
    {
        await using var ctx = await CreateContextAsync();
        await EnableReminderChannelAsync(ctx, channel);
        var now = DateTimeOffset.Parse("2026-09-08T12:00:00Z");
        var start = now.AddHours(19);
        await SeedAsync(ctx, bookedStatus, start);

        await CreateJob(ctx, now).ExecuteAsync(CancellationToken.None);
        Assert.NotEmpty(ctx.Scheduler.Scheduled);

        await SetReservationAsync(ctx, statusAtSend, start);
        var scheduled = ctx.Scheduler.Scheduled.Count;
        await ProcessQueuedAsync(ctx);
        await ProcessQueuedAsync(ctx);
        await CreateJob(ctx, now).ExecuteAsync(CancellationToken.None);

        Assert.Equal(0, ctx.Email.SendCount);
        Assert.Equal(0, ctx.WhatsApp.SendCount);
        Assert.Equal(scheduled, ctx.Scheduler.Scheduled.Count);
        var delivery = Assert.Single(
            ctx.Harness.Db.NotificationDeliveries,
            d => d.Channel == channel);
        Assert.Equal(NotificationDeliveryStatus.Failed, delivery.Status);
        Assert.Equal(ReservationReminderSendGate.SkippedMessage, delivery.ErrorMessage);
        Assert.Equal(0, delivery.AttemptCount);
        Assert.Null(delivery.NextAttemptAt);
    }

    [Fact]
    public async Task Reminder_is_not_sent_when_the_reservation_row_is_missing()
    {
        await using var ctx = await CreateContextAsync();
        await EnableReminderChannelAsync(ctx, NotificationChannel.Email);
        var now = DateTimeOffset.Parse("2026-09-08T12:00:00Z");
        await SeedAsync(ctx, ReservationStatus.Confirmed, now.AddHours(19));

        await CreateJob(ctx, now).ExecuteAsync(CancellationToken.None);
        var notification = await ctx.Harness.Db.Notifications.SingleAsync(n =>
            n.EventType == RentalEventTypes.ReservationReminder);
        notification.AggregateId = Guid.NewGuid();
        await ctx.Harness.Db.SaveChangesAsync();
        await ProcessQueuedAsync(ctx);

        Assert.Equal(0, ctx.Email.SendCount);
        var delivery = Assert.Single(
            ctx.Harness.Db.NotificationDeliveries,
            d => d.Channel == NotificationChannel.Email);
        Assert.Equal(NotificationDeliveryStatus.Failed, delivery.Status);
        Assert.Equal(ReservationReminderSendGate.SkippedMessage, delivery.ErrorMessage);
    }

    [Fact]
    public async Task Reminder_queued_for_a_future_start_is_not_sent_after_that_start_passes()
    {
        await using var ctx = await CreateContextAsync();
        await EnableReminderChannelAsync(ctx, NotificationChannel.Email);
        var now = DateTimeOffset.Parse("2026-09-08T12:00:00Z");
        var start = now.AddHours(19);
        await SeedAsync(ctx, ReservationStatus.Confirmed, start);

        await CreateJob(ctx, now).ExecuteAsync(CancellationToken.None);
        ctx.Clock.SetUtcNow(start);
        await ProcessQueuedAsync(ctx);

        Assert.Equal(0, ctx.Email.SendCount);
        var delivery = Assert.Single(
            ctx.Harness.Db.NotificationDeliveries,
            d => d.Channel == NotificationChannel.Email);
        Assert.Equal(NotificationDeliveryStatus.Failed, delivery.Status);
        Assert.Equal(ReservationReminderSendGate.SkippedMessage, delivery.ErrorMessage);
    }

    [Fact]
    public async Task Cancellation_email_still_sends_when_the_queued_reminder_is_skipped()
    {
        await using var ctx = await CreateContextAsync();
        await EnableReminderChannelAsync(ctx, NotificationChannel.Email);
        await EnableEventChannelAsync(
            ctx,
            RentalEventTypes.ReservationCanceled,
            NotificationChannel.Email);
        var now = DateTimeOffset.Parse("2026-09-08T12:00:00Z");
        var start = now.AddHours(19);
        await SeedAsync(ctx, ReservationStatus.Confirmed, start);

        await CreateJob(ctx, now).ExecuteAsync(CancellationToken.None);
        var reservation = await ctx.Harness.Db.Reservations
            .Include(r => r.Customer)
            .Include(r => r.Items)
                .ThenInclude(i => i.RentalAsset)
                    .ThenInclude(a => a.Asset)
            .SingleAsync();
        reservation.Status = ReservationStatus.Canceled;
        reservation.Touch();
        var queuedCancel = await ctx.Publisher.PublishReservationEventAsync(
            reservation,
            RentalEventTypes.ReservationCanceled,
            CancellationToken.None);
        await ctx.Harness.Db.SaveChangesAsync();
        foreach (var deliveryId in queuedCancel)
        {
            ctx.Scheduler.Schedule(deliveryId);
        }

        await ProcessQueuedAsync(ctx);

        Assert.Equal(0, ctx.WhatsApp.SendCount);
        Assert.Equal(1, ctx.Email.SendCount);
        var deliveries = await ctx.Harness.Db.NotificationDeliveries
            .Include(d => d.Notification)
            .ToListAsync();
        var reminder = Assert.Single(
            deliveries,
            d => d.Notification.EventType == RentalEventTypes.ReservationReminder
                 && d.Channel == NotificationChannel.Email);
        Assert.Equal(NotificationDeliveryStatus.Failed, reminder.Status);
        Assert.Equal(ReservationReminderSendGate.SkippedMessage, reminder.ErrorMessage);
        var cancellation = Assert.Single(
            deliveries,
            d => d.Notification.EventType == RentalEventTypes.ReservationCanceled
                 && d.Channel == NotificationChannel.Email);
        Assert.Equal(NotificationDeliveryStatus.Sent, cancellation.Status);
    }

    private static ReservationReminderJob CreateJob(Context ctx, DateTimeOffset utcNow)
    {
        ctx.Clock.SetUtcNow(utcNow);
        return new ReservationReminderJob(
            new ReminderJobTestScopeFactory(ctx.Harness.DatabaseName, ctx.Scheduler),
            ctx.Clock,
            NullLogger<ReservationReminderJob>.Instance);
    }

    private static async Task SeedAsync(
        Context ctx,
        ReservationStatus status,
        DateTimeOffset start)
    {
        ctx.Harness.SeedOverlappingReservation(status, start, start.AddHours(1));
        foreach (var entry in ctx.Harness.Db.ChangeTracker.Entries<Reservation>()
            .Where(entry => entry.State == EntityState.Added))
        {
            entry.Property(r => r.CreatedAt).CurrentValue = start.AddDays(-2);
        }
        await ctx.Harness.Db.SaveChangesAsync();
    }

    private static async Task<Context> CreateContextAsync()
    {
        var harness = await LocationBookingHarness.CreateAsync();
        var publisher = new RentalsNotificationPublisher(
            harness.Db,
            harness.TenantProvider,
            NullLogger<RentalsNotificationPublisher>.Instance);
        var scheduler = new RecordingOutboxScheduler();
        var whatsApp = new DevWhatsAppRecorder();
        var email = new DevEmailRecorder();
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-09-08T12:00:00Z"));
        var processor = new NotificationOutboxProcessor(
            harness.Db,
            email,
            whatsApp,
            NullLogger<NotificationOutboxProcessor>.Instance,
            clock);
        return new Context(harness, publisher, scheduler, processor, whatsApp, email, clock);
    }

    private static Task EnableReminderChannelAsync(Context ctx, NotificationChannel channel) =>
        channel switch
        {
            NotificationChannel.Email => EnableReminderEmailAsync(ctx),
            NotificationChannel.WhatsApp => EnableReminderWhatsAppAsync(ctx),
            _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null),
        };

    private static async Task EnableEventChannelAsync(
        Context ctx,
        string eventType,
        NotificationChannel channel)
    {
        await ctx.Publisher.EnsureReadyAsync(CancellationToken.None);
        var config = await ctx.Harness.Db.TenantNotificationChannelConfigs.FirstAsync(c =>
            c.EventType == eventType
            && c.Channel == channel);
        config.IsActive = true;
        await ctx.Harness.Db.SaveChangesAsync();
    }

    private static async Task SetReservationAsync(
        Context ctx,
        ReservationStatus status,
        DateTimeOffset start)
    {
        var reservation = await ctx.Harness.Db.Reservations.SingleAsync();
        reservation.Status = status;
        reservation.StartDateTime = start;
        reservation.Touch();
        await ctx.Harness.Db.SaveChangesAsync();
    }

    private static async Task EnableReminderEmailAsync(Context ctx)
    {
        await ctx.Publisher.EnsureReadyAsync(CancellationToken.None);
        await EnsureActiveRentalsModuleAsync(ctx.Harness.Db, ctx.Harness.TenantId);
        var config = await ctx.Harness.Db.TenantNotificationChannelConfigs.FirstAsync(c =>
            c.EventType == RentalEventTypes.ReservationReminder
            && c.Channel == NotificationChannel.Email);
        config.IsActive = true;
        await ctx.Harness.Db.SaveChangesAsync();
    }

    private static async Task EnableReminderWhatsAppAsync(Context ctx)
    {
        await ctx.Publisher.EnsureReadyAsync(CancellationToken.None);
        await EnsureActiveRentalsModuleAsync(ctx.Harness.Db, ctx.Harness.TenantId);
        var email = await ctx.Harness.Db.TenantNotificationChannelConfigs.FirstAsync(c =>
            c.EventType == RentalEventTypes.ReservationReminder
            && c.Channel == NotificationChannel.Email);
        email.IsActive = false;
        var config = await ctx.Harness.Db.TenantNotificationChannelConfigs.FirstAsync(c =>
            c.EventType == RentalEventTypes.ReservationReminder
            && c.Channel == NotificationChannel.WhatsApp);
        config.IsActive = true;
        await ctx.Harness.Db.SaveChangesAsync();
    }

    private static async Task SetReminderChannelAsync(
        AppDbContext db,
        NotificationChannel channel,
        bool isActive)
    {
        var config = await db.TenantNotificationChannelConfigs.FirstAsync(c =>
            c.EventType == RentalEventTypes.ReservationReminder
            && c.Channel == channel);
        config.IsActive = isActive;
        await db.SaveChangesAsync();
    }

    private static async Task EnsureActiveRentalsModuleAsync(AppDbContext db, Guid tenantId)
    {
        var module = await db.TenantModules.FirstOrDefaultAsync(m =>
            m.TenantId == tenantId && m.ModuleName == PlatformModules.Rentals);
        if (module is null)
        {
            db.TenantModules.Add(new TenantModule(tenantId, PlatformModules.Rentals, isActive: true));
        }
        else if (!module.IsActive)
        {
            module.Activate();
        }

        await db.SaveChangesAsync();
    }

    private static async Task SetRentalsModuleActiveAsync(AppDbContext db, Guid tenantId, bool isActive)
    {
        var module = await db.TenantModules.FirstAsync(m =>
            m.TenantId == tenantId && m.ModuleName == PlatformModules.Rentals);
        if (isActive)
        {
            module.Activate();
        }
        else
        {
            module.Deactivate();
        }

        await db.SaveChangesAsync();
    }

    private static async Task<Guid> SeedSecondTenantReminderAsync(
        Context ctx,
        DateTimeOffset start,
        NotificationChannel channel = NotificationChannel.WhatsApp,
        bool inAppActive = true)
    {
        var tenantProvider = new FakeTenantProvider();
        await using var db = InMemoryAppDb.Create(tenantProvider, ctx.Harness.DatabaseName);
        var tenant = new Tenant(
            "Clube B",
            Guid.NewGuid().ToString("N")[..14],
            subdomain: $"clube-b-{Guid.NewGuid():N}"[..20]);
        var unit = new Unit(tenant.Id, "Matriz B");
        var category = new AssetCategory { TenantId = tenant.Id, Name = "Quadras" };
        var family = new AssetFamily
        {
            Key = $"spaces-{Guid.NewGuid():N}"[..32],
            Label = "Spaces",
            FieldSchemaJson = "{}",
        };
        var asset = new Asset
        {
            TenantId = tenant.Id,
            UnitId = unit.Id,
            CategoryId = category.Id,
            FamilyId = family.Id,
            Name = "Quadra B",
            Tag = "QB",
            Status = AssetStatus.Active,
            IsRentable = true,
        };
        var rental = new RentalAsset
        {
            TenantId = tenant.Id,
            AssetId = asset.Id,
            Type = RentalAssetType.Location,
            TotalQuantity = 1,
            IsActive = true,
            RequiresDeposit = true,
            SchedulePolicy = SchedulePolicy.OpenHours,
            OpenTime = new TimeOnly(8, 0),
            CloseTime = new TimeOnly(22, 0),
            QueueEnabled = false,
        };
        var customer = new Customer
        {
            TenantId = tenant.Id,
            Name = "Cliente B",
            Email = "cliente-b@club.test",
            Phone = "11888888888",
        };
        var reservation = new Reservation
        {
            TenantId = tenant.Id,
            UnitId = unit.Id,
            CustomerId = customer.Id,
            CustomerName = "Cliente B",
            CustomerWhatsApp = "11888888888",
            StartDateTime = start,
            EndDateTime = start.AddHours(1),
            Status = ReservationStatus.Confirmed,
            TotalAmount = 100m,
            DepositPaid = 0m,
        };
        reservation.AddItem(new ReservationItem
        {
            TenantId = tenant.Id,
            ReservationId = reservation.Id,
            RentalAssetId = rental.Id,
            Quantity = 1,
            UnitPrice = 100m,
            SubTotal = 100m,
        });

        tenantProvider.TenantId = tenant.Id;
        db.Tenants.Add(tenant);
        db.TenantModules.Add(new TenantModule(tenant.Id, PlatformModules.Rentals, isActive: true));
        db.Units.Add(unit);
        db.AssetCategories.Add(category);
        db.AssetFamilies.Add(family);
        db.Assets.Add(asset);
        db.RentalAssets.Add(rental);
        db.Customers.Add(customer);
        db.Reservations.Add(reservation);
        db.Entry(reservation).Property(r => r.CreatedAt).CurrentValue = start.AddDays(-2);
        await db.SaveChangesAsync();

        var publisher = new RentalsNotificationPublisher(
            db,
            tenantProvider,
            NullLogger<RentalsNotificationPublisher>.Instance);
        await publisher.EnsureReadyAsync(CancellationToken.None);
        var config = await db.TenantNotificationChannelConfigs.FirstAsync(c =>
            c.EventType == RentalEventTypes.ReservationReminder
            && c.Channel == channel);
        config.IsActive = true;
        if (channel != NotificationChannel.Email)
        {
            var email = await db.TenantNotificationChannelConfigs.FirstAsync(c =>
                c.EventType == RentalEventTypes.ReservationReminder
                && c.Channel == NotificationChannel.Email);
            email.IsActive = false;
        }
        if (!inAppActive)
        {
            var inApp = await db.TenantNotificationChannelConfigs.FirstAsync(c =>
                c.EventType == RentalEventTypes.ReservationReminder
                && c.Channel == NotificationChannel.InApp);
            inApp.IsActive = false;
        }

        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private static async Task ProcessQueuedAsync(Context ctx)
    {
        var previousTenantId = ctx.Harness.TenantProvider.TenantId;
        ctx.Harness.TenantProvider.TenantId = null;
        try
        {
            foreach (var id in ctx.Scheduler.Scheduled.ToList())
            {
                await ctx.Processor.ProcessDeliveryAsync(id, CancellationToken.None);
            }
        }
        finally
        {
            ctx.Harness.TenantProvider.TenantId = previousTenantId;
        }
    }

    private sealed class ReminderJobTestScopeFactory(
        string databaseName,
        RecordingOutboxScheduler scheduler,
        List<(Guid? TenantId, string? PlatformScopeReason)>? reads = null) : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
        {
            var ambient = new AmbientTenantContext();
            var tenantProvider = new AmbientBackedTenantProvider(ambient, reads);
            var db = InMemoryAppDb.Create(tenantProvider, databaseName);
            var publisher = new RentalsNotificationPublisher(
            db,
            tenantProvider,
            NullLogger<RentalsNotificationPublisher>.Instance);
            return new ReminderJobTestScope(ambient, db, publisher, scheduler);
        }
    }

    /// <summary>Optionally records each tenant read (EF filter) with the active PlatformScope.</summary>
    private sealed class AmbientBackedTenantProvider(
        AmbientTenantContext ambient,
        List<(Guid? TenantId, string? PlatformScopeReason)>? reads) : ITenantProvider
    {
        public Guid? TenantId
        {
            get
            {
                reads?.Add((ambient.TenantId, PlatformScope.CurrentReason));
                return ambient.TenantId;
            }
        }
    }

    private sealed class ReminderJobTestScope(
        AmbientTenantContext ambient,
        AppDbContext db,
        IRentalsNotificationPublisher publisher,
        INotificationOutboxScheduler scheduler) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } =
            new ReminderJobTestServices(ambient, db, publisher, scheduler);

        public void Dispose() => db.Dispose();
    }

    private sealed class ReminderJobTestServices(
        AmbientTenantContext ambient,
        AppDbContext db,
        IRentalsNotificationPublisher publisher,
        INotificationOutboxScheduler scheduler) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(AmbientTenantContext))
            {
                return ambient;
            }

            if (serviceType == typeof(AppDbContext))
            {
                return db;
            }

            if (serviceType == typeof(IRentalsNotificationPublisher))
            {
                return publisher;
            }

            if (serviceType == typeof(INotificationOutboxScheduler))
            {
                return scheduler;
            }

            return null;
        }
    }

    private sealed record Context(
        LocationBookingHarness Harness,
        RentalsNotificationPublisher Publisher,
        RecordingOutboxScheduler Scheduler,
        NotificationOutboxProcessor Processor,
        DevWhatsAppRecorder WhatsApp,
        DevEmailRecorder Email,
        TestTimeProvider Clock) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Harness.DisposeAsync();
    }
}
