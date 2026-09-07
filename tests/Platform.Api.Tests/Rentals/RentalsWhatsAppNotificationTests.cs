using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Notifications;
using Platform.Api.Tests.Catalog;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.Rentals;

public sealed class RentalsWhatsAppNotificationTests
{
    [Fact]
    public async Task Pending_deposit_create_sends_status_template()
    {
        await using var ctx = await CreateContextAsync(requiresDeposit: true);
        await EnableWhatsAppAsync(ctx, RentalEventTypes.ReservationPendingDeposit);

        var created = await ctx.Reservations.CreateReservationAsync(
            ctx.Harness.CustomerId,
            CreateRequest(ctx.Harness),
            CancellationToken.None);
        await ProcessQueuedAsync(ctx);

        AssertStatusSend(ctx, "Aguardando depósito", created.CustomerWhatsApp);
        Assert.Equal(RentalEventTypes.ReservationPendingDeposit, ctx.LastEventType);
    }

    [Fact]
    public async Task Direct_confirmed_create_sends_confirmed_template()
    {
        await using var ctx = await CreateContextAsync(requiresDeposit: false);
        await EnableWhatsAppAsync(ctx, RentalEventTypes.ReservationConfirmed);

        await ctx.Reservations.CreateReservationAsync(
            ctx.Harness.CustomerId,
            CreateRequest(ctx.Harness),
            CancellationToken.None);
        await ProcessQueuedAsync(ctx);

        AssertStatusSend(ctx, "Confirmada", "11999999999");
        Assert.Equal(RentalEventTypes.ReservationConfirmed, ctx.LastEventType);
    }

    [Fact]
    public async Task Confirm_pending_deposit_sends_confirmed_and_idempotent_confirm_does_not_resend()
    {
        await using var ctx = await CreateContextAsync(requiresDeposit: true);
        await EnableWhatsAppAsync(ctx, RentalEventTypes.ReservationConfirmed);
        var reservation = ctx.Harness.SeedOverlappingReservation(ReservationStatus.PendingDeposit);
        await ctx.Harness.Db.SaveChangesAsync();

        await ctx.Reservations.ConfirmAsync(reservation.Id, CancellationToken.None);
        await ctx.Reservations.ConfirmAsync(reservation.Id, CancellationToken.None);
        await ProcessQueuedAsync(ctx);

        Assert.Equal(1, ctx.WhatsApp.SendCount);
        AssertStatusSend(ctx, "Confirmada", reservation.CustomerWhatsApp);
    }

    [Fact]
    public async Task Staff_and_customer_cancel_notify_once()
    {
        await using var ctx = await CreateContextAsync(requiresDeposit: true);
        await EnableWhatsAppAsync(ctx, RentalEventTypes.ReservationCanceled);

        var staffTarget = ctx.Harness.SeedOverlappingReservation(ReservationStatus.Confirmed);
        await ctx.Harness.Db.SaveChangesAsync();
        await ctx.Reservations.CancelAsync(staffTarget.Id, CancellationToken.None);
        await ctx.Reservations.CancelAsync(staffTarget.Id, CancellationToken.None);
        await ProcessQueuedAsync(ctx);
        Assert.Equal(1, ctx.WhatsApp.SendCount);
        AssertStatusSend(ctx, "Cancelada", staffTarget.CustomerWhatsApp);

        ctx.WhatsApp.Reset();
        var futureStart = DateTimeOffset.UtcNow.AddDays(2);
        var customerTarget = ctx.Harness.SeedOverlappingReservation(
            ReservationStatus.Confirmed,
            start: futureStart,
            end: futureStart.AddHours(1));
        await ctx.Harness.Db.SaveChangesAsync();
        await ctx.Reservations.CancelByCustomerAsync(
            ctx.Harness.CustomerId,
            customerTarget.Id,
            CancellationToken.None);
        await ctx.Reservations.CancelByCustomerAsync(
            ctx.Harness.CustomerId,
            customerTarget.Id,
            CancellationToken.None);
        await ProcessQueuedAsync(ctx);
        Assert.Equal(1, ctx.WhatsApp.SendCount);
        Assert.Equal("Cancelada", ctx.WhatsApp.LastParameters[3]);
    }

    [Fact]
    public async Task Complete_sends_completed_status()
    {
        await using var ctx = await CreateContextAsync(requiresDeposit: true);
        await EnableWhatsAppAsync(ctx, RentalEventTypes.ReservationCompleted);
        var reservation = ctx.Harness.SeedOverlappingReservation(ReservationStatus.Confirmed);
        await ctx.Harness.Db.SaveChangesAsync();

        await ctx.Reservations.CompleteAsync(reservation.Id, CancellationToken.None);
        await ProcessQueuedAsync(ctx);

        AssertStatusSend(ctx, "Concluída", reservation.CustomerWhatsApp);
    }

    [Fact]
    public async Task Failed_confirm_does_not_notify()
    {
        await using var ctx = await CreateContextAsync(requiresDeposit: true);
        await EnableWhatsAppAsync(ctx, RentalEventTypes.ReservationConfirmed);
        var reservation = ctx.Harness.SeedOverlappingReservation(ReservationStatus.Canceled);
        await ctx.Harness.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ctx.Reservations.ConfirmAsync(reservation.Id, CancellationToken.None));

        Assert.Empty(ctx.Scheduler.Scheduled);
        Assert.Equal(0, ctx.WhatsApp.SendCount);
    }

    [Fact]
    public async Task WhatsApp_channel_off_does_not_queue_whatsapp()
    {
        await using var ctx = await CreateContextAsync(requiresDeposit: true);
        await ctx.Publisher.EnsureReadyAsync(CancellationToken.None);
        await ctx.Reservations.CreateReservationAsync(
            ctx.Harness.CustomerId,
            CreateRequest(ctx.Harness),
            CancellationToken.None);

        Assert.DoesNotContain(
            ctx.Harness.Db.NotificationDeliveries,
            d => d.Channel == NotificationChannel.WhatsApp);
        Assert.Equal(0, ctx.WhatsApp.SendCount);
    }

    [Fact]
    public async Task Other_tenant_cannot_complete_and_does_not_see_deliveries()
    {
        await using var ctx = await CreateContextAsync(requiresDeposit: true);
        await EnableWhatsAppAsync(ctx, RentalEventTypes.ReservationCompleted);
        var reservation = ctx.Harness.SeedOverlappingReservation(ReservationStatus.Confirmed);
        await ctx.Harness.Db.SaveChangesAsync();

        var otherProvider = new FakeTenantProvider { TenantId = Guid.NewGuid() };
        await using var otherDb = InMemoryAppDb.Create(otherProvider);
        var otherTenant = new Tenant("Other", "55555555000191", subdomain: "other-wa");
        otherProvider.TenantId = otherTenant.Id;
        otherDb.Tenants.Add(otherTenant);
        await otherDb.SaveChangesAsync();
        var otherService = new ReservationService(
            otherDb,
            otherProvider,
            new FakeTrialGuard(),
            TestReservationQueue.Create(otherDb, otherProvider),
            new RentalsNotificationPublisher(otherDb, otherProvider),
            new NoOpNotificationOutboxScheduler());

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            otherService.CompleteAsync(reservation.Id, CancellationToken.None));
        Assert.Empty(otherDb.NotificationDeliveries);
    }

    [Fact]
    public async Task Reminder_publish_uses_reminder_template_and_sao_paulo_clock()
    {
        await using var ctx = await CreateContextAsync(requiresDeposit: true);
        await EnableWhatsAppAsync(ctx, RentalEventTypes.ReservationReminder);
        var reservation = ctx.Harness.SeedOverlappingReservation(ReservationStatus.Confirmed);
        await ctx.Harness.Db.SaveChangesAsync();
        var loaded = await ctx.Harness.Db.Reservations
            .Include(r => r.Items).ThenInclude(i => i.RentalAsset).ThenInclude(a => a.Asset)
            .FirstAsync(r => r.Id == reservation.Id);

        var queued = await ctx.Publisher.PublishReservationEventAsync(
            loaded,
            RentalEventTypes.ReservationReminder,
            CancellationToken.None);
        await ctx.Harness.Db.SaveChangesAsync();
        foreach (var id in queued)
        {
            await ctx.Processor.ProcessDeliveryAsync(id, CancellationToken.None);
        }

        Assert.Equal("rental_reservation_reminder", ctx.WhatsApp.LastTemplateName);
        Assert.Equal("pt_BR", ctx.WhatsApp.LastLanguage);
        Assert.Equal(4, ctx.WhatsApp.LastParameters.Count);
        Assert.Equal("Clube Reserva", ctx.WhatsApp.LastParameters[0]);
        Assert.Equal("Quadra 1", ctx.WhatsApp.LastParameters[2]);
        Assert.Equal(
            BrazilTimeZone.FormatCivilDateTime(LocationBookingHarness.RangeStart),
            ctx.WhatsApp.LastParameters[3]);
        Assert.Equal("01/09/2026 10:00", ctx.WhatsApp.LastParameters[3]);
        Assert.Equal(0, ctx.WhatsApp.FreeTextCount);
    }

    [Fact]
    public async Task Missing_recipient_fails_permanently_without_send()
    {
        await using var ctx = await CreateContextAsync(requiresDeposit: true);
        await EnableWhatsAppAsync(ctx, RentalEventTypes.ReservationConfirmed);
        var reservation = ctx.Harness.SeedOverlappingReservation(ReservationStatus.PendingDeposit);
        reservation.CustomerWhatsApp = " ";
        await ctx.Harness.Db.SaveChangesAsync();

        await ctx.Reservations.ConfirmAsync(reservation.Id, CancellationToken.None);
        await ProcessQueuedAsync(ctx);

        Assert.Equal(0, ctx.WhatsApp.SendCount);
        var delivery = ctx.Harness.Db.NotificationDeliveries.Single(d => d.Channel == NotificationChannel.WhatsApp);
        Assert.Equal(NotificationDeliveryStatus.Failed, delivery.Status);
        Assert.Equal(1, delivery.AttemptCount);
    }

    private static async Task<Context> CreateContextAsync(bool requiresDeposit)
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

        var publisher = new RentalsNotificationPublisher(harness.Db, harness.TenantProvider);
        var scheduler = new RecordingOutboxScheduler();
        var whatsApp = new DevWhatsAppRecorder();
        var processor = new NotificationOutboxProcessor(
            harness.Db,
            new DevEmailRecorder(),
            whatsApp,
            NullLogger<NotificationOutboxProcessor>.Instance);
        var reservations = new ReservationService(
            harness.Db,
            harness.TenantProvider,
            new FakeTrialGuard(),
            TestReservationQueue.Create(harness.Db, harness.TenantProvider),
            publisher,
            scheduler);

        return new Context(harness, publisher, scheduler, processor, whatsApp, reservations);
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

    private static async Task EnableWhatsAppAsync(Context ctx, string eventType)
    {
        await ctx.Publisher.EnsureReadyAsync(CancellationToken.None);
        var config = await ctx.Harness.Db.TenantNotificationChannelConfigs
            .FirstAsync(c => c.EventType == eventType && c.Channel == NotificationChannel.WhatsApp);
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

    private static void AssertStatusSend(Context ctx, string status, string phone)
    {
        Assert.Equal("rental_reservation_status_update", ctx.WhatsApp.LastTemplateName);
        Assert.Equal("pt_BR", ctx.WhatsApp.LastLanguage);
        Assert.Equal(4, ctx.WhatsApp.LastParameters.Count);
        Assert.Equal("Clube Reserva", ctx.WhatsApp.LastParameters[0]);
        Assert.Equal("Quadra 1", ctx.WhatsApp.LastParameters[2]);
        Assert.Equal(status, ctx.WhatsApp.LastParameters[3]);
        Assert.Equal(phone, ctx.WhatsApp.LastRecipient);
        Assert.Equal(0, ctx.WhatsApp.FreeTextCount);
    }

    private sealed record Context(
        LocationBookingHarness Harness,
        RentalsNotificationPublisher Publisher,
        RecordingOutboxScheduler Scheduler,
        NotificationOutboxProcessor Processor,
        DevWhatsAppRecorder WhatsApp,
        ReservationService Reservations) : IAsyncDisposable
    {
        public string? LastEventType =>
            Harness.Db.Notifications
                .OrderByDescending(n => n.CreatedAt)
                .Select(n => n.EventType)
                .FirstOrDefault();

        public ValueTask DisposeAsync() => Harness.DisposeAsync();
    }
}
