using Microsoft.EntityFrameworkCore;
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
    public async Task WhatsApp_channel_off_does_not_publish_reminder()
    {
        await using var ctx = await CreateContextAsync();
        await ctx.Publisher.EnsureReadyAsync(CancellationToken.None);
        var now = DateTimeOffset.Parse("2026-09-08T12:00:00Z");
        await SeedAsync(ctx, ReservationStatus.Confirmed, now.AddHours(6));

        await CreateJob(ctx, now).ExecuteAsync(CancellationToken.None);
        await ProcessQueuedAsync(ctx);

        Assert.Equal(0, ctx.WhatsApp.SendCount);
        Assert.DoesNotContain(
            ctx.Harness.Db.NotificationDeliveries,
            d => d.Channel == NotificationChannel.WhatsApp);
    }

    private static ReservationReminderJob CreateJob(Context ctx, DateTimeOffset utcNow) =>
        new(
            ctx.Harness.Db,
            ctx.Publisher,
            ctx.Scheduler,
            new AmbientTenantContext { TenantId = ctx.Harness.TenantId },
            new TestTimeProvider(utcNow),
            NullLogger<ReservationReminderJob>.Instance);

    private static async Task SeedAsync(
        Context ctx,
        ReservationStatus status,
        DateTimeOffset start)
    {
        ctx.Harness.SeedOverlappingReservation(status, start, start.AddHours(1));
        await ctx.Harness.Db.SaveChangesAsync();
    }

    private static async Task<Context> CreateContextAsync()
    {
        var harness = await LocationBookingHarness.CreateAsync();
        var publisher = new RentalsNotificationPublisher(harness.Db, harness.TenantProvider);
        var scheduler = new RecordingOutboxScheduler();
        var whatsApp = new DevWhatsAppRecorder();
        var processor = new NotificationOutboxProcessor(
            harness.Db,
            new DevEmailRecorder(),
            whatsApp,
            NullLogger<NotificationOutboxProcessor>.Instance);
        return new Context(harness, publisher, scheduler, processor, whatsApp);
    }

    private static async Task EnableReminderWhatsAppAsync(Context ctx)
    {
        await ctx.Publisher.EnsureReadyAsync(CancellationToken.None);
        var config = await ctx.Harness.Db.TenantNotificationChannelConfigs.FirstAsync(c =>
            c.EventType == RentalEventTypes.ReservationReminder
            && c.Channel == NotificationChannel.WhatsApp);
        config.IsActive = true;
        await ctx.Harness.Db.SaveChangesAsync();
    }

    private static async Task ProcessQueuedAsync(Context ctx)
    {
        foreach (var id in ctx.Scheduler.Scheduled.ToList())
        {
            await ctx.Processor.ProcessDeliveryAsync(id, CancellationToken.None);
        }
    }

    private sealed record Context(
        LocationBookingHarness Harness,
        RentalsNotificationPublisher Publisher,
        RecordingOutboxScheduler Scheduler,
        NotificationOutboxProcessor Processor,
        DevWhatsAppRecorder WhatsApp) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Harness.DisposeAsync();
    }
}
