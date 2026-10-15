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

public sealed class ReservationPastStartTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);
    private static readonly TimeOnly Eight = new(8, 0);
    private static readonly TimeOnly Nine = new(9, 0);

    [Fact]
    public async Task Create_rejects_a_past_date()
    {
        var now = BrazilTimeZone.AtLocal(Today, new TimeOnly(12, 0));
        await using var harness = await BookingHarness.CreateAsync(now);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.CreateAsync(Today.AddDays(-1), Eight, Nine));

        Assert.Equal(ReservationBookingRules.PastStartMessage, ex.Message);
        Assert.Equal(0, await harness.Db.Reservations.CountAsync());
    }

    [Fact]
    public async Task Create_rejects_today_when_the_start_is_already_past()
    {
        var now = BrazilTimeZone.AtLocal(Today, new TimeOnly(10, 0));
        await using var harness = await BookingHarness.CreateAsync(now);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.CreateAsync(Today, Eight, Nine));

        Assert.Equal(ReservationBookingRules.PastStartMessage, ex.Message);
        Assert.Equal(0, await harness.Db.Reservations.CountAsync());
    }

    [Fact]
    public async Task Create_accepts_today_when_the_start_is_still_ahead()
    {
        var now = BrazilTimeZone.AtLocal(Today, new TimeOnly(7, 0));
        await using var harness = await BookingHarness.CreateAsync(now);

        var created = await harness.CreateAsync(Today, Eight, Nine);

        Assert.Equal(BrazilTimeZone.AtLocal(Today, Eight), created.StartDateTime);
        Assert.Equal(1, await harness.Db.Reservations.CountAsync());
    }

    [Fact]
    public async Task Create_accepts_a_start_equal_to_now_and_rejects_one_tick_earlier()
    {
        var start = BrazilTimeZone.AtLocal(Today, Eight);
        await using var atStart = await BookingHarness.CreateAsync(start);
        var created = await atStart.CreateAsync(Today, Eight, Nine);
        Assert.Equal(start, created.StartDateTime);

        await using var justAfter = await BookingHarness.CreateAsync(start.AddTicks(1));
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            justAfter.CreateAsync(Today, Eight, Nine));
        Assert.Equal(ReservationBookingRules.PastStartMessage, ex.Message);
    }

    [Fact]
    public async Task Availability_marks_past_intervals_not_bookable_and_quotes_a_future_one()
    {
        var now = BrazilTimeZone.AtLocal(Today, new TimeOnly(10, 0));
        await using var harness = await BookingHarness.CreateAsync(now);

        var pastDate = await harness.Availability(Today.AddDays(-1), Eight, Nine);
        var pastToday = await harness.Availability(Today, Eight, Nine);
        var future = await harness.Availability(Today, new TimeOnly(11, 0), new TimeOnly(12, 0));
        var atNow = await harness.Availability(Today, new TimeOnly(10, 0), new TimeOnly(11, 0));

        Assert.False(pastDate.IsAvailable);
        Assert.Equal(ReservationBookingRules.PastStartMessage, pastDate.Reason);
        Assert.Null(pastDate.EstimatedTotalAmount);
        Assert.False(pastToday.IsAvailable);
        Assert.Equal(ReservationBookingRules.PastStartMessage, pastToday.Reason);
        Assert.True(future.IsAvailable);
        Assert.Null(future.Reason);
        Assert.Equal(80m, future.EstimatedTotalAmount);
        Assert.True(atNow.IsAvailable);
        Assert.Null(atNow.Reason);
    }

    [Fact]
    public async Task Availability_of_an_unknown_asset_stays_not_found_even_when_the_interval_is_past()
    {
        var now = BrazilTimeZone.AtLocal(Today, new TimeOnly(12, 0));
        await using var harness = await BookingHarness.CreateAsync(now);

        var result = await harness.Reservations.CheckAvailabilityAsync(
            new CheckAvailabilityRequestDto
            {
                AssetId = Guid.NewGuid(),
                Date = Today.AddDays(-1),
                StartTime = Eight,
                EndTime = Nine,
            },
            CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Equal("Rentable asset was not found or is inactive.", result.Reason);
    }

    [Fact]
    public async Task BookSlot_rejects_a_past_start_and_accepts_now()
    {
        var start = BrazilTimeZone.AtLocal(Today, Eight);
        await using var past = await BookingHarness.CreateAsync(start.AddMinutes(1));
        var pastSlot = await past.SeedSlotAsync(Today, Eight, Nine);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            past.BookAsync(pastSlot.Id));
        Assert.Equal(ReservationBookingRules.PastStartMessage, ex.Message);
        Assert.Equal(0, await past.Db.Reservations.CountAsync());

        await using var now = await BookingHarness.CreateAsync(start);
        var openSlot = await now.SeedSlotAsync(Today, Eight, Nine);
        var booked = await now.BookAsync(openSlot.Id);
        Assert.Equal(start, booked.StartDateTime);
    }

    [Fact]
    public async Task Sao_Paulo_evening_is_still_bookable_after_utc_midnight()
    {
        // 2026-10-15 02:00Z is 2026-10-14 23:00 in America/Sao_Paulo.
        var now = new DateTimeOffset(2026, 10, 15, 2, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 10, 14), BrazilTimeZone.GetCivilDate(now));
        Assert.Equal(now, BrazilTimeZone.AtLocal(new DateOnly(2026, 10, 14), new TimeOnly(23, 0)));

        await using var harness = await BookingHarness.CreateAsync(now);
        var civilDate = new DateOnly(2026, 10, 14);

        var earlier = await harness.Availability(civilDate, new TimeOnly(22, 0), new TimeOnly(23, 0));
        Assert.False(earlier.IsAvailable);
        Assert.Equal(ReservationBookingRules.PastStartMessage, earlier.Reason);

        var atNow = await harness.Availability(civilDate, new TimeOnly(23, 0), new TimeOnly(23, 30));
        Assert.True(atNow.IsAvailable);

        var created = await harness.CreateAsync(civilDate, new TimeOnly(23, 0), new TimeOnly(23, 30));
        Assert.Equal(now, created.StartDateTime);

        var past = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.CreateAsync(civilDate, new TimeOnly(22, 0), new TimeOnly(23, 0)));
        Assert.Equal(ReservationBookingRules.PastStartMessage, past.Message);
    }

    [Fact]
    public async Task Next_sao_paulo_morning_stays_bookable_while_utc_is_already_that_date()
    {
        // 2026-10-14 01:30Z is still 2026-10-13 22:30 in America/Sao_Paulo.
        var now = new DateTimeOffset(2026, 10, 14, 1, 30, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 10, 13), BrazilTimeZone.GetCivilDate(now));

        await using var harness = await BookingHarness.CreateAsync(now);

        var pastEvening = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.CreateAsync(new DateOnly(2026, 10, 13), new TimeOnly(22, 0), new TimeOnly(22, 30)));
        Assert.Equal(ReservationBookingRules.PastStartMessage, pastEvening.Message);

        var laterTonight = await harness.CreateAsync(
            new DateOnly(2026, 10, 13), new TimeOnly(23, 0), new TimeOnly(23, 30));
        Assert.Equal(
            BrazilTimeZone.AtLocal(new DateOnly(2026, 10, 13), new TimeOnly(23, 0)),
            laterTonight.StartDateTime);

        var nextMorning = await harness.Availability(
            new DateOnly(2026, 10, 14), Eight, Nine);
        Assert.True(nextMorning.IsAvailable);
        Assert.Null(nextMorning.Reason);
    }

    [Fact]
    public async Task Overlapping_location_create_uses_portuguese_unavailable_message()
    {
        var now = BrazilTimeZone.AtLocal(Today, new TimeOnly(7, 0));
        await using var harness = await BookingHarness.CreateAsync(now);
        await harness.CreateAsync(Today, Eight, Nine);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.CreateAsync(Today, Eight, Nine));

        Assert.Equal(
            ReservationBookingRules.LocationUnavailable(BookingHarness.AssetName),
            ex.Message);
        Assert.Equal(1, await harness.Db.Reservations.CountAsync());
    }

    private sealed class BookingHarness : IAsyncDisposable
    {
        public const string AssetName = "Quadra 1";

        private BookingHarness(
            AppDbContext db,
            Guid unitId,
            Guid customerId,
            Guid assetId,
            ReservationService reservations,
            ScheduleService schedule,
            OccupancyKindService kinds)
        {
            Db = db;
            UnitId = unitId;
            CustomerId = customerId;
            AssetId = assetId;
            Reservations = reservations;
            Schedule = schedule;
            Kinds = kinds;
        }

        public AppDbContext Db { get; }

        public Guid UnitId { get; }

        public Guid CustomerId { get; }

        public Guid AssetId { get; }

        public ReservationService Reservations { get; }

        public ScheduleService Schedule { get; }

        public OccupancyKindService Kinds { get; }

        public static async Task<BookingHarness> CreateAsync(DateTimeOffset utcNow)
        {
            var tenantProvider = new FakeTenantProvider();
            var db = InMemoryAppDb.Create(tenantProvider);
            var clock = new TestTimeProvider(utcNow);
            var tenant = new Tenant("Clube Passado", "77777777000191", subdomain: "clube-passado");
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
                Name = AssetName,
                Tag = "QP",
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
                RequiresDeposit = false,
                SchedulePolicy = SchedulePolicy.OpenHours,
                OpenTime = TimeOnly.MinValue,
                CloseTime = new TimeOnly(23, 59),
                AllowedDurationMinutes = "60",
                QueueEnabled = false,
            };

            tenantProvider.TenantId = tenant.Id;
            db.Tenants.Add(tenant);
            db.Units.Add(unit);
            db.AssetCategories.Add(category);
            db.AssetFamilies.Add(family);
            db.Assets.Add(asset);
            db.RentalAssets.Add(rental);
            foreach (var day in Enum.GetValues<DayOfWeek>())
            {
                db.RentalPricings.Add(new RentalPricing
                {
                    TenantId = tenant.Id,
                    RentalAssetId = rental.Id,
                    DayOfWeek = day,
                    StartTime = TimeOnly.MinValue,
                    EndTime = new TimeOnly(23, 59),
                    PricePerHour = 80m,
                    RequiresDeposit = false,
                    DepositPercentage = 0m,
                });
            }

            var customer = new Customer
            {
                TenantId = tenant.Id,
                Name = "Cliente",
                Email = "past@club.test",
                Phone = "11999999999",
            };
            db.Customers.Add(customer);
            await db.SaveChangesAsync();

            var kinds = new OccupancyKindService(db, tenantProvider);
            var queue = TestReservationQueue.Create(db, tenantProvider, clock);
            var reservations = new ReservationService(
                db,
                tenantProvider,
                new FakeTrialGuard(),
                queue,
                SilentRentalsNotifications.Publisher,
                SilentRentalsNotifications.Scheduler,
                clock);
            var schedule = new ScheduleService(
                db,
                tenantProvider,
                kinds,
                new FakeTrialGuard(),
                queue,
                SilentRentalsNotifications.Publisher,
                SilentRentalsNotifications.Scheduler,
                clock);

            return new BookingHarness(
                db,
                unit.Id,
                customer.Id,
                asset.Id,
                reservations,
                schedule,
                kinds);
        }

        public Task<ReservationResponseDto> CreateAsync(DateOnly date, TimeOnly start, TimeOnly end) =>
            Reservations.CreateReservationAsync(
                CustomerId,
                new CreateReservationRequestDto
                {
                    UnitId = UnitId,
                    Date = date,
                    StartTime = start,
                    EndTime = end,
                    Items = [new CreateReservationItemRequestDto { AssetId = AssetId, Quantity = 1 }],
                },
                CancellationToken.None);

        public Task<CheckAvailabilityResponseDto> Availability(DateOnly date, TimeOnly start, TimeOnly end) =>
            Reservations.CheckAvailabilityAsync(
                new CheckAvailabilityRequestDto
                {
                    AssetId = AssetId,
                    Date = date,
                    StartTime = start,
                    EndTime = end,
                },
                CancellationToken.None);

        public async Task<Slot> SeedSlotAsync(DateOnly date, TimeOnly start, TimeOnly end)
        {
            await Kinds.EnsureDefaultsAsync(CancellationToken.None);
            var openKind = await Db.OccupancyKinds.SingleAsync(kind => kind.Key == "open");
            var slot = new Slot
            {
                TenantId = openKind.TenantId,
                RentalAssetId = await Db.RentalAssets.Select(rental => rental.Id).SingleAsync(),
                Date = date,
                StartTime = start,
                EndTime = end,
                OccupancyKindId = openKind.Id,
                Status = SlotStatus.Available,
            };
            Db.Slots.Add(slot);
            await Db.SaveChangesAsync();
            return slot;
        }

        public Task<ReservationResponseDto> BookAsync(Guid slotId) =>
            Schedule.BookSlotAsync(
                CustomerId,
                new BookSlotRequestDto
                {
                    SlotId = slotId,
                    UnitId = UnitId,
                    Quantity = 1,
                },
                CancellationToken.None);

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
