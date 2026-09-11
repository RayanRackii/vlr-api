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

    private static ReservationReminderJob CreateJob(Context ctx, DateTimeOffset utcNow) =>
        new(
            new ReminderJobTestScopeFactory(ctx.Harness.DatabaseName, ctx.Scheduler),
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

    private static async Task SeedSecondTenantReminderAsync(Context ctx, DateTimeOffset start)
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
        db.Units.Add(unit);
        db.AssetCategories.Add(category);
        db.AssetFamilies.Add(family);
        db.Assets.Add(asset);
        db.RentalAssets.Add(rental);
        db.Customers.Add(customer);
        db.Reservations.Add(reservation);
        await db.SaveChangesAsync();

        var publisher = new RentalsNotificationPublisher(db, tenantProvider);
        await publisher.EnsureReadyAsync(CancellationToken.None);
        var config = await db.TenantNotificationChannelConfigs.FirstAsync(c =>
            c.EventType == RentalEventTypes.ReservationReminder
            && c.Channel == NotificationChannel.WhatsApp);
        config.IsActive = true;
        await db.SaveChangesAsync();
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
        RecordingOutboxScheduler scheduler) : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
        {
            var ambient = new AmbientTenantContext();
            var tenantProvider = new AmbientBackedTenantProvider(ambient);
            var db = InMemoryAppDb.Create(tenantProvider, databaseName);
            var publisher = new RentalsNotificationPublisher(db, tenantProvider);
            return new ReminderJobTestScope(ambient, db, publisher, scheduler);
        }
    }

    private sealed class AmbientBackedTenantProvider(AmbientTenantContext ambient) : ITenantProvider
    {
        public Guid? TenantId => ambient.TenantId;
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
        DevWhatsAppRecorder WhatsApp) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Harness.DisposeAsync();
    }
}
