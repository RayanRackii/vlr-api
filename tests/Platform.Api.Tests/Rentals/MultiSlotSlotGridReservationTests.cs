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

/// <summary>
/// One reservation covering N contiguous 30-minute SlotGrid windows (e.g. 60/90 on a 30-min grid).
/// </summary>
public sealed class MultiSlotSlotGridReservationTests
{
    private static readonly DateOnly CivilDate = new(2026, 9, 10);
    private static readonly TimeOnly Ten = new(10, 0);
    private static readonly TimeOnly TenThirty = new(10, 30);
    private static readonly TimeOnly Eleven = new(11, 0);
    private static readonly TimeOnly ElevenThirty = new(11, 30);

    [Theory]
    [InlineData(PricingUnit.Slot, 105)]
    [InlineData(PricingUnit.Hour, 52.5)]
    public async Task Window_specific_rates_are_summed(PricingUnit unit, decimal expected)
    {
        await using var h = await MultiSlotHarness.CreateAsync();
        h.AddGridWindow(Ten, TenThirty, 35m);
        h.AddGridWindow(TenThirty, Eleven, 70m);
        foreach (var pricing in h.Db.RentalPricings.Local)
            pricing.PricingUnit = unit;
        h.Rental.AllowedDurationMinutes = "60";
        await h.Db.SaveChangesAsync();

        var quote = await h.CheckAsync(Ten, Eleven);
        Assert.True(quote.IsAvailable, quote.Reason);
        Assert.Equal(expected, quote.EstimatedTotalAmount);
        Assert.Equal(expected, (await h.CreateAsync(Ten, Eleven)).TotalAmount);
    }

    [Theory]
    [InlineData(PricingUnit.Slot, 210)]
    [InlineData(PricingUnit.Hour, 105)]
    [InlineData(PricingUnit.Day, 70)]
    [InlineData(PricingUnit.Fixed, 35)]
    public async Task Full_range_price_keeps_its_unit_semantics(PricingUnit unit, decimal expected)
    {
        await using var h = await MultiSlotHarness.CreateAsync();
        h.AddGridWindow(Ten, TenThirty, 35m);
        h.AddGridWindow(TenThirty, Eleven, 35m);
        h.AddGridWindow(Eleven, ElevenThirty, 35m);
        h.Db.RentalPricings.RemoveRange(h.Db.RentalPricings.Local.ToList());
        h.Db.RentalPricings.Add(new RentalPricing
        {
            TenantId = h.TenantId, RentalAssetId = h.RentalAssetId, DayOfWeek = CivilDate.DayOfWeek,
            StartTime = Ten, EndTime = ElevenThirty, PricePerHour = 35m, PricingUnit = unit,
            RequiresDeposit = false, DepositPercentage = 0,
        });
        h.Rental.TotalQuantity = 2;
        h.Rental.AllowedDurationMinutes = "90";
        await h.Db.SaveChangesAsync();

        var quote = await h.CheckAsync(Ten, ElevenThirty, quantity: 2);
        Assert.True(quote.IsAvailable, quote.Reason);
        Assert.Equal(expected, quote.EstimatedTotalAmount);
        var created = await h.CreateAsync(Ten, ElevenThirty, quantity: 2);
        Assert.Equal(expected, created.TotalAmount);
    }

    [Fact]
    public async Task Sixty_minute_booking_spans_two_windows_and_quote_equals_create_total()
    {
        await using var h = await MultiSlotHarness.CreateAsync();
        h.AddGridWindow(Ten, TenThirty, 35m);
        h.AddGridWindow(TenThirty, Eleven, 35m);
        h.Rental.AllowedDurationMinutes = "30,60,90";
        await h.Db.SaveChangesAsync();

        var quote = await h.CheckAsync(Ten, Eleven);
        Assert.True(quote.IsAvailable, quote.Reason);
        Assert.Equal(70m, quote.EstimatedTotalAmount);

        var created = await h.CreateAsync(Ten, Eleven);
        Assert.Equal(70m, created.TotalAmount);
        Assert.Equal(TimeSpan.FromMinutes(60), created.EndDateTime - created.StartDateTime);
    }

    [Fact]
    public async Task Ninety_minute_booking_spans_three_windows_and_quote_equals_create_total()
    {
        await using var h = await MultiSlotHarness.CreateAsync();
        h.AddGridWindow(Ten, TenThirty, 35m);
        h.AddGridWindow(TenThirty, Eleven, 35m);
        h.AddGridWindow(Eleven, ElevenThirty, 35m);
        h.Rental.AllowedDurationMinutes = "30,60,90";
        await h.Db.SaveChangesAsync();

        var quote = await h.CheckAsync(Ten, ElevenThirty);
        Assert.True(quote.IsAvailable, quote.Reason);
        Assert.Equal(105m, quote.EstimatedTotalAmount);

        var created = await h.CreateAsync(Ten, ElevenThirty);
        Assert.Equal(105m, created.TotalAmount);
        Assert.Equal(TimeSpan.FromMinutes(90), created.EndDateTime - created.StartDateTime);
    }

    [Fact]
    public async Task Non_contiguous_windows_are_rejected()
    {
        await using var h = await MultiSlotHarness.CreateAsync();
        h.AddGridWindow(Ten, TenThirty, 35m);
        h.AddGridWindow(Eleven, ElevenThirty, 35m); // gap: 10:30-11:00 is missing
        h.Rental.AllowedDurationMinutes = "30,60,90";
        await h.Db.SaveChangesAsync();

        var quote = await h.CheckAsync(Ten, Eleven);
        Assert.False(quote.IsAvailable);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.CreateAsync(Ten, Eleven));
        Assert.Contains("not open for the requested interval", error.Message);
        Assert.Empty(await h.Db.Reservations.ToListAsync());
    }

    [Fact]
    public async Task One_booked_window_inside_the_range_is_rejected()
    {
        await using var h = await MultiSlotHarness.CreateAsync();
        h.AddGridWindow(Ten, TenThirty, 35m);
        h.AddGridWindow(TenThirty, Eleven, 35m);
        h.AddGridWindow(Eleven, ElevenThirty, 35m);
        h.Rental.AllowedDurationMinutes = "30,60,90";
        await h.Db.SaveChangesAsync();
        // A persisted slot overriding the middle window as already booked.
        h.Db.Slots.Add(new Slot
        {
            TenantId = h.TenantId, RentalAssetId = h.RentalAssetId, Date = CivilDate,
            StartTime = TenThirty, EndTime = Eleven, OccupancyKindId = h.OpenKindId,
            Status = SlotStatus.Booked,
        });
        await h.Db.SaveChangesAsync();

        var quote = await h.CheckAsync(Ten, ElevenThirty);
        Assert.False(quote.IsAvailable);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.CreateAsync(Ten, ElevenThirty));
        Assert.Contains("not open for the requested interval", error.Message);
    }

    [Fact]
    public async Task Duration_not_in_the_allowed_list_is_rejected()
    {
        await using var h = await MultiSlotHarness.CreateAsync();
        h.AddGridWindow(Ten, TenThirty, 35m);
        h.AddGridWindow(TenThirty, Eleven, 35m);
        h.Rental.AllowedDurationMinutes = "90"; // 60 (two windows) is not allowed
        await h.Db.SaveChangesAsync();

        var quote = await h.CheckAsync(Ten, Eleven);
        Assert.False(quote.IsAvailable);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.CreateAsync(Ten, Eleven));
    }

    [Fact]
    public async Task Duration_with_extra_seconds_does_not_match_an_allowed_whole_minute_duration()
    {
        await using var h = await MultiSlotHarness.CreateAsync();
        var end = new TimeOnly(11, 0, 30);
        h.AddGridWindow(Ten, TenThirty, 35m);
        h.AddGridWindow(TenThirty, end, 35m);
        h.Rental.AllowedDurationMinutes = "60";
        await h.Db.SaveChangesAsync();

        Assert.False((await h.CheckAsync(Ten, end)).IsAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.CreateAsync(Ten, end));
    }

    [Fact]
    public async Task Multi_window_booking_unset_allowed_durations_only_allows_a_single_window()
    {
        await using var h = await MultiSlotHarness.CreateAsync();
        h.AddGridWindow(Ten, TenThirty, 35m);
        h.AddGridWindow(TenThirty, Eleven, 35m);
        h.Rental.AllowedDurationMinutes = null;
        await h.Db.SaveChangesAsync();

        Assert.False((await h.CheckAsync(Ten, Eleven)).IsAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.CreateAsync(Ten, Eleven));

        var single = await h.CheckAsync(Ten, TenThirty);
        Assert.True(single.IsAvailable, single.Reason);
        var created = await h.CreateAsync(Ten, TenThirty);
        Assert.Equal(35m, created.TotalAmount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Multi_window_booking_ends_exactly_at_day_close(bool persisted)
    {
        await using var h = await MultiSlotHarness.CreateAsync();
        var dayCloseEnd = new TimeOnly(23, 59, 59);
        h.AddGridWindow(new TimeOnly(23, 0), new TimeOnly(23, 30), 35m);
        h.AddGridWindow(new TimeOnly(23, 30), dayCloseEnd, 35m);
        h.Rental.AllowedDurationMinutes = "30,60,90";
        if (persisted)
        {
            h.AddPersistedWindow(new TimeOnly(23, 0), new TimeOnly(23, 30));
            h.AddPersistedWindow(new TimeOnly(23, 30), dayCloseEnd);
        }
        await h.Db.SaveChangesAsync();

        var quote = await h.CheckAsync(new TimeOnly(23, 0), dayCloseEnd);
        Assert.True(quote.IsAvailable, quote.Reason);
        Assert.Equal(70m, quote.EstimatedTotalAmount);

        var created = await h.CreateAsync(new TimeOnly(23, 0), dayCloseEnd);
        Assert.Equal(70m, created.TotalAmount);
        Assert.Equal(TimeSpan.FromMinutes(60), created.EndDateTime - created.StartDateTime);
        var day = await h.Schedule.GetDayAsync(CivilDate, h.RentalAssetId, false, CancellationToken.None);
        Assert.Equal(2, day.Slots.Count);
        Assert.All(day.Slots, s =>
        {
            Assert.Equal(SlotStatus.Booked, s.Status);
            Assert.Equal(created.Id, s.ReservationId);
        });
    }

    [Fact]
    public async Task Multi_window_booking_succeeds_on_a_DST_transition_day()
    {
        await using var h = await MultiSlotHarness.CreateAsync();
        await h.UseNewYorkTimeAsync();
        var dstDate = new DateOnly(2026, 3, 8);
        h.AddGridWindow(Ten, TenThirty, 35m, dstDate.DayOfWeek);
        h.AddGridWindow(TenThirty, Eleven, 35m, dstDate.DayOfWeek);
        h.Rental.AllowedDurationMinutes = "30,60,90";
        await h.Db.SaveChangesAsync();

        var quote = await h.CheckAsync(Ten, Eleven, dstDate);
        Assert.True(quote.IsAvailable, quote.Reason);
        Assert.Equal(70m, quote.EstimatedTotalAmount);

        var created = await h.CreateAsync(Ten, Eleven, dstDate);
        Assert.Equal(70m, created.TotalAmount);
        Assert.Equal(TimeSpan.FromMinutes(60), created.EndDateTime - created.StartDateTime);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 14, 0, 0, TimeSpan.Zero), created.StartDateTime);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_releases_the_full_multi_window_range(bool persisted)
    {
        await using var h = await MultiSlotHarness.CreateAsync();
        h.AddGridWindow(Ten, TenThirty, 35m);
        h.AddGridWindow(TenThirty, Eleven, 35m);
        h.Rental.AllowedDurationMinutes = "30,60,90";
        if (persisted)
        {
            h.AddPersistedWindow(Ten, TenThirty);
            h.AddPersistedWindow(TenThirty, Eleven);
        }
        await h.Db.SaveChangesAsync();

        var created = await h.CreateAsync(Ten, Eleven);
        var bookedDay = await h.Schedule.GetDayAsync(CivilDate, h.RentalAssetId, false, CancellationToken.None);
        Assert.Equal(2, bookedDay.Slots.Count(s => s.Status == SlotStatus.Booked && s.ReservationId == created.Id));
        Assert.Equal(persisted ? 2 : 0, bookedDay.Slots.Count(s => s.Status == SlotStatus.Booked && !s.IsDerived));
        Assert.False((await h.CheckAsync(Ten, TenThirty)).IsAvailable);
        Assert.False((await h.CheckAsync(TenThirty, Eleven)).IsAvailable);

        var canceled = await h.Reservations.CancelByCustomerAsync(h.CustomerId, created.Id, CancellationToken.None);
        Assert.Equal(ReservationStatus.Canceled, canceled.Status);

        Assert.True((await h.CheckAsync(Ten, TenThirty)).IsAvailable);
        Assert.True((await h.CheckAsync(TenThirty, Eleven)).IsAvailable);
        Assert.True((await h.CheckAsync(Ten, Eleven)).IsAvailable);
        var releasedDay = await h.Schedule.GetDayAsync(CivilDate, h.RentalAssetId, false, CancellationToken.None);
        Assert.All(releasedDay.Slots, s =>
        {
            Assert.Equal(SlotStatus.Available, s.Status);
            Assert.Null(s.ReservationId);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Staff_day_view_shows_a_booked_row_per_covered_window_for_the_same_reservation(int persistedCount)
    {
        await using var h = await MultiSlotHarness.CreateAsync();
        h.AddGridWindow(Ten, TenThirty, 35m);
        h.AddGridWindow(TenThirty, Eleven, 35m);
        h.AddGridWindow(Eleven, ElevenThirty, 35m);
        h.Rental.AllowedDurationMinutes = "30,60,90";
        var cells = new[] { (Ten, TenThirty), (TenThirty, Eleven), (Eleven, ElevenThirty) };
        foreach (var (start, end) in cells.Take(persistedCount))
            h.AddPersistedWindow(start, end);
        await h.Db.SaveChangesAsync();

        var created = await h.CreateAsync(Ten, ElevenThirty);

        var day = await h.Schedule.GetDayAsync(CivilDate, h.RentalAssetId, customerFacing: false, CancellationToken.None);
        var booked = day.Slots.Where(s => s.Status == SlotStatus.Booked).OrderBy(s => s.StartTime).ToList();

        Assert.Equal(3, booked.Count);
        Assert.All(booked, s => Assert.Equal(created.Id, s.ReservationId));
        Assert.Equal([Ten, TenThirty, Eleven], booked.Select(s => s.StartTime));
        Assert.Equal([TenThirty, Eleven, ElevenThirty], booked.Select(s => s.EndTime));
        Assert.Equal(persistedCount, booked.Count(s => !s.IsDerived));
        Assert.Equal(3, day.Slots.Count);

        var publicDay = await h.Schedule.GetDayAsync(CivilDate, h.RentalAssetId, true, CancellationToken.None);
        Assert.Empty(publicDay.Slots);
        foreach (var (start, end) in cells)
            Assert.False((await h.CheckAsync(start, end)).IsAvailable);

        var availability = new DailyAvailabilityService(h.Db, h.TenantProvider,
            new TestTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero)));
        var daily = await availability.GetAsync(CivilDate, [h.RentalAssetId], null, 1, null, CancellationToken.None);
        var resource = Assert.Single(daily.Resources);
        Assert.Equal(3, resource.Windows.Count);
        Assert.All(resource.Windows, w =>
        {
            Assert.Equal(AvailabilityWindowState.Taken, w.State);
            Assert.Equal(0, w.AvailableQuantity);
        });
    }

    [Fact]
    public async Task Single_window_booking_is_unchanged()
    {
        await using var h = await MultiSlotHarness.CreateAsync();
        h.AddGridWindow(Ten, TenThirty, 35m);
        h.Rental.AllowedDurationMinutes = "90";
        await h.Db.SaveChangesAsync();

        var quote = await h.CheckAsync(Ten, TenThirty);
        Assert.True(quote.IsAvailable, quote.Reason);
        Assert.Equal(35m, quote.EstimatedTotalAmount);

        var created = await h.CreateAsync(Ten, TenThirty);
        Assert.Equal(35m, created.TotalAmount);
        Assert.Equal(TimeSpan.FromMinutes(30), created.EndDateTime - created.StartDateTime);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    public async Task Staff_day_keeps_the_full_reserved_range_after_weekly_templates_move(int persistedCount, int movedCount)
    {
        await using var h = await MultiSlotHarness.CreateAsync();
        h.AddGridWindow(Ten, TenThirty, 35m);
        h.AddGridWindow(TenThirty, Eleven, 35m);
        h.AddGridWindow(Eleven, ElevenThirty, 35m);
        if (persistedCount > 0)
            h.AddPersistedWindow(Ten, TenThirty);
        h.Rental.AllowedDurationMinutes = "90";
        await h.Db.SaveChangesAsync();
        var created = await h.CreateAsync(Ten, ElevenThirty);

        foreach (var template in h.Db.ScheduleTemplates.Local.OrderByDescending(t => t.StartTime).Take(movedCount))
        {
            template.StartTime = template.StartTime.AddHours(3);
            template.EndTime = template.EndTime.AddHours(3);
        }
        await h.Db.SaveChangesAsync();

        var day = await h.Schedule.GetDayAsync(CivilDate, h.RentalAssetId, false, CancellationToken.None);
        var booked = day.Slots.Where(s => s.Status == SlotStatus.Booked).OrderBy(s => s.StartTime).ToList();
        Assert.NotEmpty(booked);
        Assert.Equal(Ten, booked[0].StartTime);
        Assert.Equal(ElevenThirty, booked[^1].EndTime);
        Assert.All(booked, s => Assert.Equal(created.Id, s.ReservationId));
        for (var i = 1; i < booked.Count; i++)
            Assert.Equal(booked[i - 1].EndTime, booked[i].StartTime);
    }

    [Fact]
    public async Task SlotGrid_rentable_can_save_allowed_duration_minutes_not_a_multiple_of_slot_length()
    {
        await using var h = await MultiSlotHarness.CreateAsync();
        var service = new RentalAssetService(h.Db, h.TenantProvider);

        var response = await service.UpdateSchedulePolicyAsync(
            h.RentalAssetId,
            new UpdateRentalSchedulePolicyRequestDto
            {
                SchedulePolicy = SchedulePolicy.SlotGrid,
                AllowedDurationMinutes = "40,70",
            },
            CancellationToken.None);

        Assert.Equal("40,70", response.AllowedDurationMinutes);
    }

    private sealed class MultiSlotHarness : IAsyncDisposable
    {
        private static readonly TimeProvider BookingClock =
            new TestTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));

        private MultiSlotHarness(
            AppDbContext db,
            FakeTenantProvider tenantProvider,
            Guid tenantId,
            Guid unitId,
            Guid assetId,
            Guid rentalAssetId,
            Guid customerId,
            Guid openKindId,
            RentalAsset rental,
            ReservationService reservations,
            ScheduleService schedule)
        {
            Db = db;
            TenantProvider = tenantProvider;
            TenantId = tenantId;
            UnitId = unitId;
            AssetId = assetId;
            RentalAssetId = rentalAssetId;
            CustomerId = customerId;
            OpenKindId = openKindId;
            Rental = rental;
            Reservations = reservations;
            Schedule = schedule;
        }

        public AppDbContext Db { get; }
        public FakeTenantProvider TenantProvider { get; }
        public Guid TenantId { get; }
        public Guid UnitId { get; }
        public Guid AssetId { get; }
        public Guid RentalAssetId { get; }
        public Guid CustomerId { get; }
        public Guid OpenKindId { get; }
        public RentalAsset Rental { get; }
        public ReservationService Reservations { get; }
        public ScheduleService Schedule { get; }

        public static async Task<MultiSlotHarness> CreateAsync()
        {
            var tenantProvider = new FakeTenantProvider();
            var db = InMemoryAppDb.Create(tenantProvider);
            var tenant = new Tenant("Grid Club", "77777777000191", subdomain: "grid-club");
            var unit = new Unit(tenant.Id, "Matriz");
            var category = new AssetCategory { TenantId = tenant.Id, Name = "Courts" };
            var family = new AssetFamily
            {
                Key = $"grid-{Guid.NewGuid():N}"[..32],
                Label = "Grid",
                FieldSchemaJson = "{}",
            };
            var asset = new Asset
            {
                TenantId = tenant.Id,
                UnitId = unit.Id,
                CategoryId = category.Id,
                FamilyId = family.Id,
                Name = "Court Grid",
                Tag = "CGRID",
                Status = AssetStatus.Active,
                IsRentable = true,
            };
            var rental = new RentalAsset
            {
                TenantId = tenant.Id,
                AssetId = asset.Id,
                Type = RentalAssetType.Good,
                TotalQuantity = 1,
                IsActive = true,
                RequiresDeposit = false,
                SchedulePolicy = SchedulePolicy.SlotGrid,
            };
            var customer = new Customer
            {
                TenantId = tenant.Id,
                Name = "Cliente Grid",
                Email = "grid@club.test",
                Phone = "11988887777",
            };
            var openKind = new OccupancyKind
            {
                TenantId = tenant.Id, Key = "open", Label = "Open", IsActive = true,
                IsBookableByCustomer = true, BlocksCapacity = false, SortOrder = 0,
            };

            tenantProvider.TenantId = tenant.Id;
            db.Tenants.Add(tenant);
            db.Units.Add(unit);
            db.AssetCategories.Add(category);
            db.AssetFamilies.Add(family);
            db.Assets.Add(asset);
            db.RentalAssets.Add(rental);
            db.Customers.Add(customer);
            db.OccupancyKinds.Add(openKind);
            await db.SaveChangesAsync();

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
                new OccupancyKindService(db, tenantProvider),
                new FakeTrialGuard(),
                queue,
                SilentRentalsNotifications.Publisher.AsDomainEvents(),
                SilentRentalsNotifications.Scheduler,
                BookingClock);

            return new MultiSlotHarness(
                db, tenantProvider, tenant.Id, unit.Id, asset.Id, rental.Id, customer.Id, openKind.Id,
                rental, reservations, schedule);
        }

        public void AddGridWindow(TimeOnly start, TimeOnly end, decimal pricePerSlot, DayOfWeek? dayOfWeek = null)
        {
            var day = dayOfWeek ?? CivilDate.DayOfWeek;
            Db.ScheduleTemplates.Add(new ScheduleTemplate
            {
                TenantId = TenantId, RentalAssetId = RentalAssetId, DayOfWeek = day,
                StartTime = start, EndTime = end, OccupancyKindId = OpenKindId, IsActive = true,
            });
            Db.RentalPricings.Add(new RentalPricing
            {
                TenantId = TenantId, RentalAssetId = RentalAssetId, DayOfWeek = day,
                StartTime = start, EndTime = end, PricePerHour = pricePerSlot,
                PricingUnit = PricingUnit.Slot, RequiresDeposit = false, DepositPercentage = 0,
            });
        }

        public void AddPersistedWindow(TimeOnly start, TimeOnly end) => Db.Slots.Add(new Slot
        {
            TenantId = TenantId, RentalAssetId = RentalAssetId, Date = CivilDate,
            StartTime = start, EndTime = end, OccupancyKindId = OpenKindId, Status = SlotStatus.Available,
        });

        public Task<CheckAvailabilityResponseDto> CheckAsync(
            TimeOnly start, TimeOnly end, DateOnly? date = null, int quantity = 1) =>
            Reservations.CheckAvailabilityAsync(new CheckAvailabilityRequestDto
            {
                AssetId = AssetId, Date = date ?? CivilDate, StartTime = start, EndTime = end, Quantity = quantity,
            }, CancellationToken.None);

        public Task<ReservationResponseDto> CreateAsync(
            TimeOnly start, TimeOnly end, DateOnly? date = null, int quantity = 1) =>
            Reservations.CreateReservationAsync(CustomerId, new CreateReservationRequestDto
            {
                UnitId = UnitId,
                Date = date ?? CivilDate,
                StartTime = start,
                EndTime = end,
                Items = [new CreateReservationItemRequestDto { AssetId = AssetId, Quantity = quantity }],
            }, CancellationToken.None);

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
