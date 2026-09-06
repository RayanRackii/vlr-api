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
    private static readonly DateOnly CivilDate = new(2026, 9, 10);
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
                queue);
            var schedule = new ScheduleService(
                db,
                tenantProvider,
                kinds,
                new FakeTrialGuard(),
                queue);

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

        public async Task<Slot> SeedAvailableSlotAsync(TimeOnly start, TimeOnly end)
        {
            await Kinds.EnsureDefaultsAsync(CancellationToken.None);
            var openKind = await Db.OccupancyKinds.SingleAsync(k => k.Key == "open");
            var slot = new Slot
            {
                TenantId = TenantId,
                RentalAssetId = RentalAssetId,
                Date = CivilDate,
                StartTime = start,
                EndTime = end,
                OccupancyKindId = openKind.Id,
                Status = SlotStatus.Available,
            };
            Db.Slots.Add(slot);
            await Db.SaveChangesAsync();
            return slot;
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
