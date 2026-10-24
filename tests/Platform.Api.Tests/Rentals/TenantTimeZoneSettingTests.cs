using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.Rentals;

/// <summary>
/// The <see cref="TenantSettingKeys.BookingTimeZoneId"/> tenant setting drives the agenda day:
/// past-slot filtering and reserved windows use the tenant's civil clock, not São Paulo or UTC.
/// </summary>
public sealed class TenantTimeZoneSettingTests
{
    private static readonly DateOnly Day = AvailabilityHarness.Day;

    // Tokyo (UTC+9): at 01:30 local the UTC date is still the previous day.
    // Manaus (UTC-4): at 21:30 local the UTC date is already the next day.
    // São Paulo would show every slot in the Tokyo case and none in the Manaus case.
    [Theory]
    [InlineData("Asia/Tokyo", 0, 3, 1, 30, -1, "02:00")]
    [InlineData("America/Manaus", 20, 23, 21, 30, 1, "22:00")]
    public async Task Public_day_hides_slots_already_past_on_the_tenant_clock(
        string zoneId, int open, int close, int nowHour, int nowMinute, int utcDayOffset, string expectedStart)
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        await SetZoneAsync(h.Db, h.Tenant.Id, zoneId);
        h.Primary.OpenTime = new(open, 0);
        h.Primary.CloseTime = new(close, 0);
        await h.Db.SaveChangesAsync();

        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        h.Clock.SetUtcNow(TenantBookingClock.AtLocal(Day, new TimeOnly(nowHour, nowMinute), zone));
        Assert.Equal(Day.AddDays(utcDayOffset), DateOnly.FromDateTime(h.Clock.GetUtcNow().UtcDateTime));

        var publicDay = await Schedule(h).GetDayAsync(Day, h.Primary.Id, customerFacing: true, default);
        Assert.Equal([TimeOnly.Parse(expectedStart)], publicDay.Slots.Select(s => s.StartTime));

        var adminDay = await Schedule(h).GetDayAsync(Day, h.Primary.Id, customerFacing: false, default);
        Assert.Equal(close - open, adminDay.Slots.Count);
    }

    [Fact]
    public async Task Reserved_windows_use_the_tenant_civil_day()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        await SetZoneAsync(h.Db, h.Tenant.Id, tokyo.Id);

        // Tokyo 10:00-11:00 is 22:00-23:00 of the previous day in São Paulo.
        var customer = new Customer { TenantId = h.Tenant.Id, Name = "Ada", Phone = "5511000000000" };
        var reservation = new Reservation { TenantId = h.Tenant.Id, UnitId = h.Unit.Id, CustomerId = customer.Id,
            CustomerName = "Ada", CustomerWhatsApp = "5511000000000", Status = Core.Domain.Enums.ReservationStatus.Confirmed,
            StartDateTime = TenantBookingClock.AtLocal(Day, new(10, 0), tokyo),
            EndDateTime = TenantBookingClock.AtLocal(Day, new(11, 0), tokyo), TotalAmount = 20, DepositPaid = 0 };
        reservation.AddItem(new ReservationItem { TenantId = h.Tenant.Id, ReservationId = reservation.Id,
            RentalAssetId = h.Primary.Id, Quantity = h.Primary.TotalQuantity, UnitPrice = 20, SubTotal = 20 });
        h.Db.AddRange(customer, reservation);
        await h.Db.SaveChangesAsync();
        Assert.Equal(Day.AddDays(-1), BrazilTimeZone.GetCivilDate(reservation.StartDateTime));

        var adminDay = await Schedule(h).GetDayAsync(Day, h.Primary.Id, customerFacing: false, default);
        Assert.Equal([new TimeOnly(11, 0)], adminDay.Slots.Select(s => s.StartTime));
    }

    [Fact]
    public async Task Missing_setting_falls_back_to_Sao_Paulo_and_the_ambient_tenant_does_not_hide_it()
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider { TenantId = Guid.NewGuid() });
        var tenantId = Guid.NewGuid();

        Assert.Null(await TenantBookingClock.ReadConfiguredTimeZoneIdAsync(db, tenantId, default));
        Assert.Equal(BrazilTimeZone.Resolve().Id,
            (await TenantBookingClock.ResolveForTenantAsync(db, tenantId, default)).Id);

        await SetZoneAsync(db, tenantId, "America/Manaus");
        Assert.Equal("America/Manaus", (await TenantBookingClock.ResolveForTenantAsync(db, tenantId, default)).Id);
    }

    [Fact]
    public async Task Invalid_setting_logs_a_warning_and_falls_back_to_Sao_Paulo()
    {
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .UseLoggerFactory(loggerFactory)
            .Options;
        await using var db = new TestAppDbContext(options, new FakeTenantProvider());
        var tenantId = Guid.NewGuid();
        await SetZoneAsync(db, tenantId, "Not/AZone");

        var zone = await TenantBookingClock.ResolveForTenantAsync(db, tenantId, default);

        Assert.Equal(BrazilTimeZone.Resolve().Id, zone.Id);
        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Warning
            && entry.Category.EndsWith(nameof(TenantBookingClock), StringComparison.Ordinal)
            && entry.Message.Contains("Not/AZone", StringComparison.Ordinal));
    }

    private static async Task SetZoneAsync(AppDbContext db, Guid tenantId, string zoneId)
    {
        db.TenantSettings.Add(new TenantSetting
        {
            TenantId = tenantId,
            Key = TenantSettingKeys.BookingTimeZoneId,
            Value = zoneId,
        });
        await db.SaveChangesAsync();
    }

    private static ScheduleService Schedule(AvailabilityHarness h) =>
        new(h.Db, h.TenantProvider, new OccupancyKindService(h.Db, h.TenantProvider), new FakeTrialGuard(),
            TestReservationQueue.Create(h.Db, h.TenantProvider, h.Clock),
            SilentRentalsNotifications.Publisher.AsDomainEvents(), SilentRentalsNotifications.Scheduler, h.Clock);
}
