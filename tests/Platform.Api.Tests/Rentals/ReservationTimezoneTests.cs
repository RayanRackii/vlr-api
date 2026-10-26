using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.Rentals;

public sealed class ReservationTimezoneTests
{
    [Theory]
    [InlineData(false, 10, 27, 30, 40, 20, 30, 3)]
    [InlineData(true, 10, 27, 0, 50, 50, 60, 4)]
    [InlineData(true, 10, 31, 0, 50, 50, 60, 4)]
    [InlineData(true, 11, 1, 0, 50, 50, 60, 5)]
    public async Task Day_close_template_quotes_and_stores_the_full_interval(
        bool newYork, int month, int day, int startMinute, decimal rate, decimal expected,
        int minutes, int utcEndHour)
    {
        await using var h = await TimezoneBookingHarness.CreateAsync();
        if (newYork) await h.UseNewYorkTimeAsync();
        var date = new DateOnly(2026, month, day);
        var start = new TimeOnly(23, startMinute);
        var end = new TimeOnly(23, 59, 59);
        await h.ConfigureDayCloseAsync(date, start, end, PricingUnit.Hour, rate);

        var available = await h.Reservations.CheckAvailabilityAsync(new CheckAvailabilityRequestDto
        {
            AssetId = h.AssetId, Date = date, StartTime = start, EndTime = end, Quantity = 1,
        }, CancellationToken.None);
        Assert.True(available.IsAvailable, available.Reason);
        Assert.Equal(expected, available.EstimatedTotalAmount);

        var created = await h.Reservations.CreateReservationAsync(h.CustomerId,
            h.CreateRequest(start, end) with { Date = date }, CancellationToken.None);
        Assert.Equal(expected, created.TotalAmount);
        Assert.Equal(TimeSpan.FromMinutes(minutes), created.EndDateTime - created.StartDateTime);
        var stored = Assert.Single(await h.Reservations.ListMineAsync(h.CustomerId, CancellationToken.None));
        var next = date.AddDays(1);
        Assert.Equal(new DateTimeOffset(next.Year, next.Month, next.Day, utcEndHour, 0, 0, TimeSpan.Zero),
            stored.EndDateTime);
    }

    [Fact]
    public async Task Day_close_OpenHours_accepts_the_full_allowed_duration()
    {
        await using var h = await TimezoneBookingHarness.CreateAsync();
        var start = new TimeOnly(23, 30);
        var end = new TimeOnly(23, 59, 59);
        await h.ConfigureDayCloseAsync(CivilDate, start, end, PricingUnit.Hour, 40m);
        var rental = await h.Db.RentalAssets.SingleAsync();
        rental.SchedulePolicy = SchedulePolicy.OpenHours;
        rental.OpenTime = new TimeOnly(23, 0);
        rental.CloseTime = end;
        rental.AllowedDurationMinutes = "30";
        await h.Db.SaveChangesAsync();

        var available = await h.Reservations.CheckAvailabilityAsync(new CheckAvailabilityRequestDto
        {
            AssetId = h.AssetId, Date = CivilDate, StartTime = start, EndTime = end, Quantity = 1,
        }, CancellationToken.None);
        Assert.True(available.IsAvailable, available.Reason);
        Assert.Equal(20m, available.EstimatedTotalAmount);
        var daily = Assert.Single((await h.Daily.GetAsync(CivilDate, [h.RentalAssetId],
            h.UnitId, 1, 30, CancellationToken.None)).Resources).Windows;
        Assert.Equal(2, daily.Count);
        Assert.Contains(daily, w => w.StartTime == start && w.EndTime == end && w.TotalAmount == 20m);
        var agenda = await h.Schedule.GetDayAsync(CivilDate, h.RentalAssetId, false, CancellationToken.None);
        Assert.Equal(2, agenda.Slots.Count);
        Assert.Contains(agenda.Slots, w => w.StartTime == start && w.EndTime == end);
        var created = await h.Reservations.CreateReservationAsync(h.CustomerId,
            h.CreateRequest(start, end), CancellationToken.None);
        Assert.Equal(20m, created.TotalAmount);
    }

    [Theory]
    [InlineData(false, PricingUnit.Hour, 40, 20)]
    [InlineData(false, PricingUnit.Slot, 35, 35)]
    [InlineData(false, PricingUnit.Day, 80, 80)]
    [InlineData(false, PricingUnit.Fixed, 15, 15)]
    [InlineData(true, PricingUnit.Hour, 40, 20)]
    [InlineData(true, PricingUnit.Slot, 35, 35)]
    [InlineData(true, PricingUnit.Day, 80, 80)]
    [InlineData(true, PricingUnit.Fixed, 15, 15)]
    public async Task Day_close_daily_quotes_and_both_booking_routes_preserve_pricing_units(
        bool persisted, PricingUnit unit, decimal rate, decimal expected)
    {
        await using var h = await TimezoneBookingHarness.CreateAsync();
        var start = new TimeOnly(23, 30);
        var end = new TimeOnly(23, 59, 59);
        await h.ConfigureDayCloseAsync(CivilDate, start, end, unit, rate);
        var slot = persisted ? await h.SeedAvailableSlotAsync(start, end) : null;
        var window = Assert.Single(Assert.Single((await h.Daily.GetAsync(CivilDate,
            [h.RentalAssetId], h.UnitId, 1, 30, CancellationToken.None)).Resources).Windows);
        Assert.Equal(end, window.EndTime);
        Assert.Equal(AvailabilityWindowState.Available, window.State);
        Assert.Equal(expected, window.TotalAmount);
        Assert.Equal(unit, window.PricingUnit);

        var created = slot is not null
            ? await h.Schedule.BookSlotAsync(h.CustomerId, new BookSlotRequestDto
            {
                SlotId = slot.Id, UnitId = h.UnitId, Quantity = 1, ExpectedTotalAmount = expected,
            }, CancellationToken.None)
            : await h.Reservations.CreateReservationAsync(h.CustomerId,
                h.CreateRequest(start, end) with { ExpectedTotalAmount = expected }, CancellationToken.None);
        Assert.Equal(expected, created.TotalAmount);
        Assert.Equal(TimeSpan.FromMinutes(30), created.EndDateTime - created.StartDateTime);
        var taken = Assert.Single(Assert.Single((await h.Daily.GetAsync(CivilDate,
            [h.RentalAssetId], h.UnitId, 1, 30, CancellationToken.None)).Resources).Windows);
        Assert.Equal(AvailabilityWindowState.Taken, taken.State);
        var booked = Assert.Single((await h.Schedule.GetDayAsync(CivilDate, h.RentalAssetId,
            false, CancellationToken.None)).Slots);
        Assert.Equal(SlotStatus.Booked, booked.Status);
        Assert.Equal(created.Id, booked.ReservationId);
        Assert.Equal(end, booked.EndTime);
    }

    [Theory]
    [InlineData(9990000)]
    [InlineData(9999999)]
    public async Task Day_close_fractional_second_matches_the_template_and_pricing(int fractionalTicks)
    {
        await using var h = await TimezoneBookingHarness.CreateAsync();
        var start = new TimeOnly(23, 30);
        var end = new TimeOnly(23, 59, 59);
        await h.ConfigureDayCloseAsync(CivilDate, start, end, PricingUnit.Hour, 40m);
        var fractionalEnd = end.Add(TimeSpan.FromTicks(fractionalTicks));
        var available = await h.Reservations.CheckAvailabilityAsync(new CheckAvailabilityRequestDto
        {
            AssetId = h.AssetId, Date = CivilDate, StartTime = start, EndTime = fractionalEnd, Quantity = 1,
        }, CancellationToken.None);
        Assert.True(available.IsAvailable, available.Reason);
        Assert.Equal(20m, available.EstimatedTotalAmount);
        var created = await h.Reservations.CreateReservationAsync(h.CustomerId,
            h.CreateRequest(start, fractionalEnd), CancellationToken.None);
        Assert.Equal(20m, created.TotalAmount);
        Assert.Equal(TimeSpan.FromMinutes(30), created.EndDateTime - created.StartDateTime);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Day_close_is_exclusive_and_cancel_only_releases_its_own_interval(bool persisted)
    {
        await using var h = await TimezoneBookingHarness.CreateAsync();
        var start = new TimeOnly(23, 30);
        var end = new TimeOnly(23, 59, 59);
        await h.ConfigureDayCloseAsync(CivilDate, start, end, PricingUnit.Hour, 40m);
        await h.SeedAvailableSlotAsync(new TimeOnly(23, 0), start);
        if (persisted) await h.SeedAvailableSlotAsync(start, end);
        await h.SeedAvailableSlotAsync(TimeOnly.MinValue, new TimeOnly(0, 30), CivilDate.AddDays(1));
        h.Db.RentalPricings.Add(new RentalPricing
        {
            TenantId = h.TenantId, RentalAssetId = h.RentalAssetId,
            DayOfWeek = CivilDate.AddDays(1).DayOfWeek, StartTime = TimeOnly.MinValue,
            EndTime = new TimeOnly(1, 0), PricePerHour = 40m, RequiresDeposit = false, DepositPercentage = 0,
        });
        await h.Db.SaveChangesAsync();

        Task<CheckAvailabilityResponseDto> Check(DateOnly date, TimeOnly from, TimeOnly to) =>
            h.Reservations.CheckAvailabilityAsync(new CheckAvailabilityRequestDto
            {
                AssetId = h.AssetId, Date = date, StartTime = from, EndTime = to, Quantity = 1,
            }, CancellationToken.None);

        // Only the exact template/slot window is bookable; 23:59:58 is not day-close.
        Assert.False((await Check(CivilDate, start, new TimeOnly(23, 59, 58))).IsAvailable);
        var late = await h.Reservations.CreateReservationAsync(h.CustomerId,
            h.CreateRequest(start, end), CancellationToken.None);
        Assert.False((await Check(CivilDate, start, end)).IsAvailable);
        Assert.True((await Check(CivilDate, new TimeOnly(23, 0), start)).IsAvailable);
        Assert.True((await Check(CivilDate.AddDays(1), TimeOnly.MinValue, new TimeOnly(0, 30))).IsAvailable);
        var nextDay = await h.Schedule.GetDayAsync(CivilDate.AddDays(1), h.RentalAssetId,
            false, CancellationToken.None);
        Assert.DoesNotContain(nextDay.Slots, w => w.ReservationId == late.Id);

        var early = await h.Reservations.CreateReservationAsync(h.CustomerId,
            h.CreateRequest(new TimeOnly(23, 0), start), CancellationToken.None);
        var midnight = await h.Reservations.CreateReservationAsync(h.CustomerId,
            h.CreateRequest(TimeOnly.MinValue, new TimeOnly(0, 30)) with { Date = CivilDate.AddDays(1) },
            CancellationToken.None);
        Assert.Equal(3, (await h.Reservations.ListMineAsync(h.CustomerId, CancellationToken.None)).Count);

        var canceled = await h.Reservations.CancelByCustomerAsync(h.CustomerId, late.Id, CancellationToken.None);
        Assert.Equal(ReservationStatus.Canceled, canceled.Status);
        Assert.Equal(late.EndDateTime, canceled.EndDateTime);
        Assert.True((await Check(CivilDate, start, end)).IsAvailable);
        Assert.False((await Check(CivilDate, new TimeOnly(23, 0), start)).IsAvailable);
        Assert.False((await Check(CivilDate.AddDays(1), TimeOnly.MinValue, new TimeOnly(0, 30))).IsAvailable);
        var remaining = await h.Reservations.ListMineAsync(h.CustomerId, CancellationToken.None);
        Assert.All(remaining.Where(r => r.Id == early.Id || r.Id == midnight.Id),
            r => Assert.NotEqual(ReservationStatus.Canceled, r.Status));
    }

    [Theory]
    [InlineData(7, 0, 8, 0, "not open for the requested interval")]
    [InlineData(23, 0, 23, 30, "not open for the requested interval")]
    [InlineData(10, 0, 10, 30, "duration is not allowed")]
    [InlineData(10, 0, 11, 0, null)]
    public async Task BookSlotAsync_enforces_OpenHours_for_persisted_slots(
        int startHour, int startMinute, int endHour, int endMinute, string? expectedError)
    {
        await using var h = await TimezoneBookingHarness.CreateAsync();
        var slot = await h.SeedAvailableSlotAsync(
            new TimeOnly(startHour, startMinute), new TimeOnly(endHour, endMinute));
        var request = new BookSlotRequestDto { SlotId = slot.Id, UnitId = h.UnitId, Quantity = 1 };

        if (expectedError is null)
        {
            var booked = await h.Schedule.BookSlotAsync(h.CustomerId, request, CancellationToken.None);
            Assert.NotEqual(Guid.Empty, booked.Id);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                h.Schedule.BookSlotAsync(h.CustomerId, request, CancellationToken.None));
            Assert.Contains(expectedError, error.Message);
            Assert.Empty(await h.Db.Reservations.ToListAsync());
            Assert.Equal(SlotStatus.Available, slot.Status);
        }
    }

    [Theory]
    [InlineData(9, 30, 10, 30, "60", null)]
    [InlineData(8, 0, 9, 0, "60", null)]
    [InlineData(22, 0, 23, 0, "60", null)]
    [InlineData(7, 30, 8, 30, "60", "not open for the requested interval")]
    [InlineData(22, 30, 23, 30, "60", "not open for the requested interval")]
    [InlineData(10, 0, 10, 30, "60", "duration is not allowed")]
    [InlineData(10, 0, 11, 0, null, null)]
    [InlineData(10, 0, 10, 30, null, "duration is not allowed")]
    [InlineData(10, 0, 11, 0, "bad,0,-1,1440", "duration is not allowed")]
    [InlineData(10, 0, 10, 30, "bad,0,-1,1440", "duration is not allowed")]
    [InlineData(10, 0, 10, 30, " 30,60,30,bad ", null)]
    public async Task OpenHours_create_and_availability_enforce_civil_hours_and_durations(
        int startHour, int startMinute, int endHour, int endMinute,
        string? durations, string? expectedError)
    {
        await using var h = await TimezoneBookingHarness.CreateAsync();
        var rental = await h.Db.RentalAssets.SingleAsync();
        rental.AllowedDurationMinutes = durations;
        await h.Db.SaveChangesAsync();
        var start = new TimeOnly(startHour, startMinute);
        var end = new TimeOnly(endHour, endMinute);

        var availability = await h.Reservations.CheckAvailabilityAsync(
            new CheckAvailabilityRequestDto
            {
                AssetId = h.AssetId, Date = CivilDate,
                StartTime = start, EndTime = end, Quantity = 1,
            }, CancellationToken.None);

        Assert.Equal(expectedError is null, availability.IsAvailable);
        if (expectedError is null)
        {
            var created = await h.Reservations.CreateReservationAsync(
                h.CustomerId, h.CreateRequest(start, end), CancellationToken.None);
            Assert.NotEqual(Guid.Empty, created.Id);
        }
        else
        {
            Assert.Contains(expectedError, availability.Reason!);
            Assert.Null(availability.EstimatedTotalAmount);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                h.Reservations.CreateReservationAsync(
                    h.CustomerId, h.CreateRequest(start, end), CancellationToken.None));
            Assert.Contains(expectedError, error.Message);
            Assert.Empty(await h.Db.Reservations.ToListAsync());
        }
    }

    [Theory]
    [InlineData(null, 23)]
    [InlineData(8, null)]
    [InlineData(8, 8)]
    [InlineData(23, 8)]
    public async Task OpenHours_missing_or_invalid_hours_are_not_bookable(int? openHour, int? closeHour)
    {
        await using var h = await TimezoneBookingHarness.CreateAsync();
        var rental = await h.Db.RentalAssets.SingleAsync();
        rental.OpenTime = openHour.HasValue ? new TimeOnly(openHour.Value, 0) : null;
        rental.CloseTime = closeHour.HasValue ? new TimeOnly(closeHour.Value, 0) : null;
        await h.Db.SaveChangesAsync();

        var availability = await h.Reservations.CheckAvailabilityAsync(
            new CheckAvailabilityRequestDto
            {
                AssetId = h.AssetId, Date = CivilDate,
                StartTime = Ten, EndTime = Eleven, Quantity = 1,
            }, CancellationToken.None);
        Assert.False(availability.IsAvailable);
        Assert.Contains("not open for the requested interval", availability.Reason!);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Reservations.CreateReservationAsync(
                h.CustomerId, h.CreateRequest(Ten, Eleven), CancellationToken.None));
        Assert.Contains("not open for the requested interval", error.Message);
        Assert.Empty(await h.Db.Reservations.ToListAsync());
    }

    [Fact]
    public async Task OpenHours_duration_uses_wall_clock_minutes_in_tenant_zone_across_DST()
    {
        await using var h = await TimezoneBookingHarness.CreateAsync();
        await h.UseNewYorkTimeAsync();
        var rental = await h.Db.RentalAssets.SingleAsync();
        rental.OpenTime = new TimeOnly(1, 0);
        rental.CloseTime = new TimeOnly(4, 0);
        rental.AllowedDurationMinutes = "120";
        var pricing = await h.Db.RentalPricings.SingleAsync();
        pricing.DayOfWeek = NewYorkSpringForward.DayOfWeek;
        pricing.StartTime = new TimeOnly(1, 0);
        pricing.EndTime = new TimeOnly(4, 0);
        await h.Db.SaveChangesAsync();
        var request = h.CreateRequest(new TimeOnly(1, 30), new TimeOnly(3, 30)) with
        {
            Date = NewYorkSpringForward,
        };

        var availability = await h.Reservations.CheckAvailabilityAsync(
            new CheckAvailabilityRequestDto
            {
                AssetId = h.AssetId, Date = request.Date,
                StartTime = request.StartTime, EndTime = request.EndTime, Quantity = 1,
            }, CancellationToken.None);
        Assert.True(availability.IsAvailable);
        var created = await h.Reservations.CreateReservationAsync(
            h.CustomerId, request, CancellationToken.None);
        AssertEqualUtc(created.StartDateTime, 2026, 3, 8, 6, 30);
        AssertEqualUtc(created.EndDateTime, 2026, 3, 8, 7, 30);
    }

    [Fact]
    public async Task SlotGrid_does_not_apply_OpenHours_duration_or_opening_hours()
    {
        await using var h = await TimezoneBookingHarness.CreateAsync();
        var rental = await h.Db.RentalAssets.SingleAsync();
        rental.SchedulePolicy = SchedulePolicy.SlotGrid;
        rental.OpenTime = null;
        rental.CloseTime = null;
        await h.Db.SaveChangesAsync();
        var end = new TimeOnly(10, 30);
        await h.SeedAvailableSlotAsync(Ten, end);

        var availability = await h.Reservations.CheckAvailabilityAsync(
            new CheckAvailabilityRequestDto
            {
                AssetId = h.AssetId, Date = CivilDate,
                StartTime = Ten, EndTime = end, Quantity = 1,
            }, CancellationToken.None);
        Assert.True(availability.IsAvailable);
        var created = await h.Reservations.CreateReservationAsync(
            h.CustomerId, h.CreateRequest(Ten, end), CancellationToken.None);
        Assert.Equal(TimeSpan.FromMinutes(30), created.EndDateTime - created.StartDateTime);
    }

    private static readonly DateOnly NewYorkSpringForward = new(2026, 3, 8);
    private static readonly TimeOnly GapStart = new(2, 30);
    private static readonly TimeOnly GapEnd = new(3, 0);

    [Fact]
    public async Task CheckAvailabilityAsync_rejects_interval_inverted_by_DST_gap_conversion()
    {
        await using var h = await TimezoneBookingHarness.CreateAsync();
        await h.UseNewYorkTimeAsync();

        var request = new CheckAvailabilityRequestDto
        {
            AssetId = h.AssetId,
            Date = NewYorkSpringForward,
            StartTime = GapStart,
            EndTime = GapEnd,
            Quantity = 1,
        };

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Reservations.CheckAvailabilityAsync(request, CancellationToken.None));

        Assert.Equal("EndTime must be greater than StartTime.", error.Message);
    }

    [Fact]
    public async Task CreateReservationAsync_does_not_persist_interval_inverted_by_DST_gap_conversion()
    {
        await using var h = await TimezoneBookingHarness.CreateAsync();
        await h.UseNewYorkTimeAsync();
        var request = h.CreateRequest(GapStart, GapEnd) with
        {
            Date = NewYorkSpringForward,
        };

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Reservations.CreateReservationAsync(
                h.CustomerId,
                request,
                CancellationToken.None));

        Assert.Equal("EndTime must be greater than StartTime.", error.Message);
        Assert.Empty(await h.Db.Reservations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task BookSlotAsync_rejects_interval_inverted_by_DST_gap_conversion()
    {
        await using var h = await TimezoneBookingHarness.CreateAsync();
        await h.UseNewYorkTimeAsync();
        var slot = await h.SeedAvailableSlotAsync(
            GapStart, GapEnd, NewYorkSpringForward);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Schedule.BookSlotAsync(
                h.CustomerId,
                new BookSlotRequestDto
                {
                    SlotId = slot.Id,
                    UnitId = h.UnitId,
                    Quantity = 1,
                },
                CancellationToken.None));

        Assert.Equal("End time must be after start time.", error.Message);
    }

    [Theory]
    [InlineData(SchedulePolicy.OpenHours)]
    [InlineData(SchedulePolicy.SlotGrid)]
    public async Task GetDayAsync_omits_interval_inverted_by_DST_gap_conversion(
        SchedulePolicy policy)
    {
        await using var h = await TimezoneBookingHarness.CreateAsync();
        await h.UseNewYorkTimeAsync();
        await h.Kinds.EnsureDefaultsAsync(CancellationToken.None);
        var rental = await h.Db.RentalAssets.SingleAsync();
        rental.SchedulePolicy = policy;
        rental.OpenTime = GapStart;
        rental.CloseTime = GapEnd;
        rental.AllowedDurationMinutes = "30";
        if (policy == SchedulePolicy.SlotGrid)
        {
            var open = await h.Db.OccupancyKinds.SingleAsync(k => k.Key == "open");
            h.Db.ScheduleTemplates.Add(new ScheduleTemplate
            {
                TenantId = h.TenantId,
                RentalAssetId = h.RentalAssetId,
                DayOfWeek = NewYorkSpringForward.DayOfWeek,
                StartTime = GapStart,
                EndTime = GapEnd,
                OccupancyKindId = open.Id,
                IsActive = true,
            });
        }
        await h.Db.SaveChangesAsync();

        var day = await h.Schedule.GetDayAsync(
            NewYorkSpringForward,
            h.RentalAssetId,
            customerFacing: false,
            CancellationToken.None);

        Assert.Empty(day.Slots);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetDayAsync_omits_persisted_interval_inverted_by_DST_gap_conversion(
        bool customerFacing)
    {
        await using var h = await TimezoneBookingHarness.CreateAsync();
        await h.UseNewYorkTimeAsync();
        await h.SeedAvailableSlotAsync(GapStart, GapEnd, NewYorkSpringForward);

        var day = await h.Schedule.GetDayAsync(
            NewYorkSpringForward,
            h.RentalAssetId,
            customerFacing,
            CancellationToken.None);

        Assert.DoesNotContain(day.Slots, slot =>
            !slot.IsDerived
            && slot.StartTime == GapStart
            && slot.EndTime == GapEnd);
    }

    [Fact]
    public async Task Admin_day_filter_uses_Manaus_midnight_for_both_bounds()
    {
        await using var h = await TimezoneBookingHarness.CreateAsync();
        h.Db.TenantSettings.Add(new TenantSetting
        {
            TenantId = h.TenantId,
            Key = Platform.Core.Domain.Constants.TenantSettingKeys.BookingTimeZoneId,
            Value = "America/Manaus",
        });
        await h.Db.SaveChangesAsync();
        var created = await h.Reservations.CreateReservationAsync(
            h.CustomerId, h.CreateRequest(TwentyTwo, TwentyThree), CancellationToken.None);
        // 22:00 Manaus = 02:00Z on the following UTC date.
        AssertEqualUtc(created.StartDateTime, 2026, 9, 11, 2, 0);
        var row = await h.Db.Reservations.SingleAsync();
        row.StartDateTime = new DateTimeOffset(2026, 9, 11, 3, 15, 0, TimeSpan.Zero);
        row.EndDateTime = row.StartDateTime.AddMinutes(30);
        await h.Db.SaveChangesAsync();
        Assert.Contains(await h.Reservations.ListAdminAsync(CivilDate, CivilDate, null, null,
            CancellationToken.None), r => r.Id == created.Id);
        Assert.DoesNotContain(await h.Reservations.ListAdminAsync(CivilDate.AddDays(1),
            CivilDate.AddDays(1), null, null, CancellationToken.None), r => r.Id == created.Id);
    }

    private static readonly DateOnly CivilDate = new(2026, 9, 10);
    private static readonly TimeProvider BookingClock =
        new TestTimeProvider(new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.Zero));
    private static readonly TimeOnly Ten = new(10, 0);
    private static readonly TimeOnly Eleven = new(11, 0);
    private static readonly TimeOnly TwentyTwo = new(22, 0);
    private static readonly TimeOnly TwentyThree = new(23, 0);

    [Fact]
    public async Task CreateReservationAsync_civil_10_to_11_persists_utc_instants_and_one_hour()
    {
        await using var harness = await TimezoneBookingHarness.CreateAsync();
        var created = await harness.Reservations.CreateReservationAsync(
            harness.CustomerId,
            harness.CreateRequest(Ten, Eleven),
            CancellationToken.None);

        AssertEqualUtc(created.StartDateTime, 2026, 9, 10, 13, 0);
        AssertEqualUtc(created.EndDateTime, 2026, 9, 10, 14, 0);
        Assert.Equal(1d, (created.EndDateTime - created.StartDateTime).TotalHours);

        var json = JsonSerializer.Serialize(created);
        Assert.DoesNotContain("10:00:00+00:00", json, StringComparison.Ordinal);
        Assert.Contains("13:00:00", json, StringComparison.Ordinal);
        Assert.True(
            json.Contains("13:00:00Z", StringComparison.Ordinal)
            || json.Contains("13:00:00+00:00", StringComparison.Ordinal),
            json);
    }

    [Fact]
    public async Task BookSlotAsync_civil_10_to_11_matches_create_reservation_instants()
    {
        await using var harness = await TimezoneBookingHarness.CreateAsync();
        var slot = await harness.SeedAvailableSlotAsync(Ten, Eleven);
        var booked = await harness.Schedule.BookSlotAsync(
            harness.CustomerId,
            new BookSlotRequestDto
            {
                SlotId = slot.Id,
                UnitId = harness.UnitId,
                Quantity = 1,
            },
            CancellationToken.None);

        AssertEqualUtc(booked.StartDateTime, 2026, 9, 10, 13, 0);
        AssertEqualUtc(booked.EndDateTime, 2026, 9, 10, 14, 0);
        Assert.Equal(
            BrazilTimeZone.AtLocal(CivilDate, Ten),
            booked.StartDateTime);
        Assert.Equal(
            BrazilTimeZone.AtLocal(CivilDate, Eleven),
            booked.EndDateTime);
    }

    [Fact]
    public async Task ListAdminAsync_includes_22_00_booking_on_civil_date_despite_utc_date_crossing()
    {
        await using var harness = await TimezoneBookingHarness.CreateAsync();
        var created = await harness.Reservations.CreateReservationAsync(
            harness.CustomerId,
            harness.CreateRequest(TwentyTwo, TwentyThree),
            CancellationToken.None);

        AssertEqualUtc(created.StartDateTime, 2026, 9, 11, 1, 0);

        var listed = await harness.Reservations.ListAdminAsync(
            CivilDate,
            CivilDate,
            status: null,
            assetId: null,
            CancellationToken.None);

        Assert.Contains(listed, row => row.Id == created.Id);
    }

    [Fact]
    public async Task GetDayAsync_late_booking_blocks_matching_civil_cell()
    {
        await using var harness = await TimezoneBookingHarness.CreateAsync();
        await harness.Kinds.EnsureDefaultsAsync(CancellationToken.None);
        await harness.Reservations.CreateReservationAsync(
            harness.CustomerId,
            harness.CreateRequest(TwentyTwo, TwentyThree),
            CancellationToken.None);

        var day = await harness.Schedule.GetDayAsync(
            CivilDate,
            harness.RentalAssetId,
            customerFacing: false,
            CancellationToken.None);

        var lateAvailable = day.Slots.Where(slot =>
            slot.StartTime == TwentyTwo
            && slot.EndTime == TwentyThree
            && slot.Status == SlotStatus.Available
            && slot.IsBookableByCustomer);
        Assert.Empty(lateAvailable);

        Assert.Contains(
            day.Slots,
            slot =>
                slot.StartTime == Ten
                && slot.Status == SlotStatus.Available
                && slot.IsBookableByCustomer);
    }

    [Fact]
    public async Task ConfirmAsync_does_not_rewrite_start_or_end_instants()
    {
        await using var harness = await TimezoneBookingHarness.CreateAsync();
        var created = await harness.Reservations.CreateReservationAsync(
            harness.CustomerId,
            harness.CreateRequest(Ten, Eleven),
            CancellationToken.None);
        var start = created.StartDateTime;
        var end = created.EndDateTime;

        var confirmed = await harness.Reservations.ConfirmAsync(
            created.Id,
            CancellationToken.None);

        Assert.Equal(start, confirmed.StartDateTime);
        Assert.Equal(end, confirmed.EndDateTime);
    }

    [Fact]
    public async Task CreateReservationAsync_22_00_still_prices_civil_thursday_window()
    {
        await using var harness = await TimezoneBookingHarness.CreateAsync();
        var created = await harness.Reservations.CreateReservationAsync(
            harness.CustomerId,
            harness.CreateRequest(TwentyTwo, TwentyThree),
            CancellationToken.None);

        Assert.Equal(DayOfWeek.Thursday, CivilDate.DayOfWeek);
        Assert.Equal(100m, created.TotalAmount);
    }

    private static void AssertEqualUtc(
        DateTimeOffset value,
        int year,
        int month,
        int day,
        int hour,
        int minute)
    {
        Assert.Equal(TimeSpan.Zero, value.Offset);
        Assert.Equal(
            new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc),
            value.UtcDateTime);
    }

    private sealed class TimezoneBookingHarness : IAsyncDisposable
    {
        private TimezoneBookingHarness(
            AppDbContext db,
            Guid tenantId,
            Guid unitId,
            Guid customerId,
            Guid assetId,
            Guid rentalAssetId,
            ReservationService reservations,
            ScheduleService schedule,
            OccupancyKindService kinds)
        {
            Db = db;
            TenantId = tenantId;
            UnitId = unitId;
            CustomerId = customerId;
            AssetId = assetId;
            RentalAssetId = rentalAssetId;
            Reservations = reservations;
            Schedule = schedule;
            Kinds = kinds;
        }

        public AppDbContext Db { get; }

        public Guid TenantId { get; }

        public Guid UnitId { get; }

        public Guid CustomerId { get; }

        public Guid AssetId { get; }

        public Guid RentalAssetId { get; }

        public ReservationService Reservations { get; }

        public ScheduleService Schedule { get; }

        public OccupancyKindService Kinds { get; }

        public DailyAvailabilityService Daily => new(Db,
            new FakeTenantProvider { TenantId = TenantId }, BookingClock);

        public static async Task<TimezoneBookingHarness> CreateAsync()
        {
            var tenantProvider = new FakeTenantProvider();
            var db = InMemoryAppDb.Create(tenantProvider);
            var tenant = new Tenant("Clube TZ", "88888888000191", subdomain: "clube-tz");
            var unit = new Unit(tenant.Id, "Matriz");
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
                Name = "Quadra TZ",
                Tag = "QTZ",
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
                CloseTime = new TimeOnly(23, 0),
                AllowedDurationMinutes = "60",
                QueueEnabled = false,
            };
            var pricing = new RentalPricing
            {
                TenantId = tenant.Id,
                RentalAssetId = rental.Id,
                DayOfWeek = CivilDate.DayOfWeek,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(23, 0),
                PricePerHour = 100m,
                RequiresDeposit = true,
                DepositPercentage = 50m,
            };
            var customer = new Customer
            {
                TenantId = tenant.Id,
                Name = "Cliente TZ",
                Email = "tz@club.test",
                Phone = "11999999999",
            };

            tenantProvider.TenantId = tenant.Id;
            db.Tenants.Add(tenant);
            db.Units.Add(unit);
            db.AssetCategories.Add(category);
            db.AssetFamilies.Add(family);
            db.Assets.Add(asset);
            db.RentalAssets.Add(rental);
            db.RentalPricings.Add(pricing);
            db.Customers.Add(customer);
            await db.SaveChangesAsync();

            var kinds = new OccupancyKindService(db, tenantProvider);
            var queue = TestReservationQueue.Create(db, tenantProvider);
            var reservations = new ReservationService(
                db,
                tenantProvider,
                new FakeTrialGuard(),
                queue,
                SilentRentalsNotifications.Publisher.AsDomainEvents(),
                SilentRentalsNotifications.Scheduler,
                BookingClock);
            var schedule = new ScheduleService(
                db,
                tenantProvider,
                kinds,
                new FakeTrialGuard(),
                queue,
                SilentRentalsNotifications.Publisher.AsDomainEvents(),
                SilentRentalsNotifications.Scheduler,
                BookingClock);

            return new TimezoneBookingHarness(
                db,
                tenant.Id,
                unit.Id,
                customer.Id,
                asset.Id,
                rental.Id,
                reservations,
                schedule,
                kinds);
        }

        public CreateReservationRequestDto CreateRequest(TimeOnly start, TimeOnly end) =>
            new()
            {
                UnitId = UnitId,
                Date = CivilDate,
                StartTime = start,
                EndTime = end,
                Items =
                [
                    new CreateReservationItemRequestDto { AssetId = AssetId, Quantity = 1 }
                ],
            };

        public async Task<Slot> SeedAvailableSlotAsync(
            TimeOnly start,
            TimeOnly end,
            DateOnly? date = null)
        {
            await Kinds.EnsureDefaultsAsync(CancellationToken.None);
            var openKind = await Db.OccupancyKinds.SingleAsync(k => k.Key == "open");
            var slot = new Slot
            {
                TenantId = TenantId,
                RentalAssetId = RentalAssetId,
                Date = date ?? CivilDate,
                StartTime = start,
                EndTime = end,
                OccupancyKindId = openKind.Id,
                Status = SlotStatus.Available,
            };
            Db.Slots.Add(slot);
            await Db.SaveChangesAsync();
            return slot;
        }

        public async Task ConfigureDayCloseAsync(DateOnly date, TimeOnly start, TimeOnly end,
            PricingUnit unit, decimal price)
        {
            var rental = await Db.RentalAssets.SingleAsync();
            rental.SchedulePolicy = SchedulePolicy.SlotGrid;
            var pricing = await Db.RentalPricings.SingleAsync();
            pricing.DayOfWeek = date.DayOfWeek;
            pricing.StartTime = TimeOnly.MinValue;
            pricing.EndTime = end;
            pricing.PricePerHour = price;
            pricing.PricingUnit = unit;
            await Kinds.EnsureDefaultsAsync(CancellationToken.None);
            var kind = await Db.OccupancyKinds.SingleAsync(k => k.Key == "open");
            Db.ScheduleTemplates.Add(new ScheduleTemplate
            {
                TenantId = TenantId, RentalAssetId = RentalAssetId, DayOfWeek = date.DayOfWeek,
                StartTime = start, EndTime = end, OccupancyKindId = kind.Id, IsActive = true,
            });
            await Db.SaveChangesAsync();
        }

        public async Task UseNewYorkTimeAsync()
        {
            Db.TenantSettings.Add(new TenantSetting
            {
                TenantId = TenantId,
                Key = Platform.Core.Domain.Constants.TenantSettingKeys.BookingTimeZoneId,
                Value = "America/New_York",
            });
            await Db.SaveChangesAsync();
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
