using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Modules.ResourcePlanning;
using Platform.Api.Modules.ResourcePlanning.Dtos;
using Platform.Api.Modules.ResourcePlanning.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.ResourcePlanning;

/// <summary>
/// The rentals bridge (spec slice API-4): a manual allocation on a rental-backed asset writes the
/// allocation and the same <c>InternalUse</c> reservation the catalog Phase 0 flow writes, through
/// one port in one transaction, and every transition keeps the two stores consistent. Customer
/// reservations block an allocation and the reverse; the rental schedule rules apply; the agenda
/// shows customer reservations as external busy time. In-memory (locks are a no-op there; the
/// serialization is covered on PostgreSQL in <c>AssetOccupancyBridgeConcurrencyTests</c>).
/// </summary>
public sealed class AssetOccupancyBridgeTests
{
    // 2026-11-23 is a Monday; rentals open 08:00-22:00 local (Sao Paulo, UTC-3, no DST).
    private static readonly DateOnly Day = new(2026, 11, 23);
    private static readonly DateTimeOffset WorkStart = BrazilTimeZone.AtLocal(Day, new TimeOnly(10, 0));
    private static readonly DateTimeOffset WorkEnd = BrazilTimeZone.AtLocal(Day, new TimeOnly(11, 0));
    private static readonly Guid Creator = Guid.NewGuid();

    [Fact]
    public async Task Manual_allocation_creates_exactly_one_internal_use_reservation_linked_by_reservation_item_id()
    {
        await using var rig = await Rig.CreateAsync();

        var created = await rig.Service.CreateAsync(Request(rig, WorkStart, WorkEnd), default);

        var allocation = await rig.Db.AssetAllocations.AsNoTracking().SingleAsync();
        var reservation = await rig.Db.Reservations.AsNoTracking().SingleAsync();
        var item = await rig.Db.ReservationItems.AsNoTracking().SingleAsync();
        Assert.NotNull(allocation.ReservationItemId);
        Assert.Equal(item.Id, allocation.ReservationItemId!.Value);
        Assert.Equal(rig.RentalAssetId, item.RentalAssetId);
        Assert.Equal(reservation.Id, item.ReservationId);
        Assert.Equal(1, item.Quantity);
        Assert.Equal(ReservationPurpose.InternalUse, reservation.Purpose);
        Assert.Equal(ReservationStatus.Confirmed, reservation.Status);
        Assert.Null(reservation.CustomerId);
        Assert.Equal(allocation.OccupiedStart, reservation.StartDateTime);
        Assert.Equal(allocation.OccupiedEnd, reservation.EndDateTime);
        Assert.Equal(rig.Seed.UnitId, reservation.UnitId);
        Assert.Equal("Manual", created.Source);
        Assert.True(created.IsBlocking);
    }

    [Fact]
    public async Task Allocation_on_a_planning_only_asset_writes_no_reservation()
    {
        await using var rig = await Rig.CreateAsync();

        await rig.Service.CreateAsync(Request(rig, WorkStart, WorkEnd, assetId: rig.Seed.SecondAssetId), default);

        var allocation = await rig.Db.AssetAllocations.AsNoTracking().SingleAsync();
        Assert.Null(allocation.ReservationItemId);
        Assert.Empty(await rig.Db.Reservations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task An_allocation_blocks_a_customer_booking_of_the_same_window()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await rig.Service.CreateAsync(Request(rig, WorkStart, WorkEnd), default);
        var allocation = await rig.Db.AssetAllocations.AsNoTracking().SingleAsync();

        // The rentals availability query is the one a customer booking is checked against.
        var blocked = await rig.Rentals.CheckAvailabilityAsync(
            rig.Db, rig.Seed.TenantId, rig.RentalAssetId, allocation.OccupiedStart, allocation.OccupiedEnd, [], default);
        Assert.False(blocked.IsAvailable);
        var reservation = await rig.Db.Reservations.AsNoTracking().SingleAsync();
        Assert.Equal(reservation.Id, blocked.ConflictReservationId);

        // Half-open: the window right after is free.
        var after = await rig.Rentals.CheckAvailabilityAsync(
            rig.Db, rig.Seed.TenantId, rig.RentalAssetId, WorkEnd, WorkEnd.AddHours(1), [], default);
        Assert.True(after.IsAvailable);
        Assert.NotEqual(Guid.Empty, created.Id);
    }

    [Fact]
    public async Task A_customer_booking_blocks_an_allocation_and_the_preview_says_so()
    {
        await using var rig = await Rig.CreateAsync();
        rig.AddReservation(ReservationPurpose.CustomerBooking, WorkStart.AddMinutes(30), WorkEnd.AddMinutes(30));
        await rig.Db.SaveChangesAsync();

        var preview = await rig.Service.PreviewAsync(Request(rig, WorkStart, WorkEnd), default);
        Assert.False(preview.Feasible);
        Assert.Null(preview.HoursViolation);
        var previewed = Assert.Single(preview.Conflicts);
        Assert.Equal("reservation", previewed.Source);

        var ex = await Assert.ThrowsAsync<PlanningException>(
            () => rig.Service.CreateAsync(Request(rig, WorkStart, WorkEnd), default));

        Assert.Equal(409, ex.StatusCode);
        Assert.Equal(PlanningException.ConflictCode, ex.Code);
        var conflict = Assert.Single(ex.Conflicts!);
        Assert.Equal("reservation", conflict.Source);
        Assert.Null(conflict.AllocationId);
        Assert.Null(conflict.Label);
        Assert.Equal(WorkStart.AddMinutes(30), conflict.Start);
        Assert.Empty(await rig.Db.AssetAllocations.AsNoTracking().ToListAsync());
        Assert.Single(await rig.Db.Reservations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Another_allocation_is_reported_as_an_allocation_conflict_with_its_title()
    {
        await using var rig = await Rig.CreateAsync();
        var first = await rig.Service.CreateAsync(Request(rig, WorkStart, WorkEnd, title: "Job A"), default);

        var ex = await Assert.ThrowsAsync<PlanningException>(
            () => rig.Service.CreateAsync(Request(rig, WorkStart, WorkEnd, title: "Job B"), default));

        var conflict = Assert.Single(ex.Conflicts!);
        Assert.Equal("allocation", conflict.Source);
        Assert.Equal(first.Id, conflict.AllocationId);
        Assert.Equal("Job A", conflict.Label);
        Assert.Single(await rig.Db.Reservations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Cancel_frees_the_slot_in_both_stores_and_the_window_can_be_allocated_again()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await rig.Service.CreateAsync(Request(rig, WorkStart, WorkEnd), default);
        await rig.AssertStoresAsync(AllocationStatus.Scheduled, ReservationStatus.Confirmed);
        Assert.False(await rig.IsFreeAsync(WorkStart, WorkEnd));

        var cancelled = await rig.Service.ApplyUsageAsync(created.Id, Usage("cancel", created.Version, "not needed"), default);

        Assert.Equal("Cancelled", cancelled.Status);
        await rig.AssertStoresAsync(AllocationStatus.Cancelled, ReservationStatus.Canceled);
        Assert.True(await rig.IsFreeAsync(WorkStart, WorkEnd));

        var again = await rig.Service.CreateAsync(Request(rig, WorkStart, WorkEnd), default);
        Assert.Equal("Scheduled", again.Status);
        Assert.Equal(2, await rig.Db.Reservations.AsNoTracking().CountAsync());
        Assert.False(await rig.IsFreeAsync(WorkStart, WorkEnd));
    }

    [Fact]
    public async Task Release_after_start_keeps_both_stores_consistent_at_every_transition_and_frees_the_unused_tail()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await rig.Service.CreateAsync(Request(rig, WorkStart, WorkEnd), default);
        await rig.AssertStoresAsync(AllocationStatus.Scheduled, ReservationStatus.Confirmed);

        rig.Clock.SetUtcNow(WorkStart.AddMinutes(5));
        var running = await rig.Service.ApplyUsageAsync(created.Id, Usage("start", created.Version), default);
        Assert.Equal("Running", running.Status);
        await rig.AssertStoresAsync(AllocationStatus.Running, ReservationStatus.Confirmed);
        Assert.False(await rig.IsFreeAsync(WorkStart, WorkEnd));

        var cleaning = await rig.Service.ApplyUsageAsync(created.Id, Usage("work-finished", running.Version), default);
        Assert.Equal("Cleaning", cleaning.Status);
        await rig.AssertStoresAsync(AllocationStatus.Cleaning, ReservationStatus.Confirmed);

        rig.Clock.SetUtcNow(WorkStart.AddMinutes(30));
        var released = await rig.Service.ApplyUsageAsync(created.Id, Usage("release", cleaning.Version), default);
        Assert.Equal("Released", released.Status);
        await rig.AssertStoresAsync(AllocationStatus.Released, ReservationStatus.Completed);

        // Released 30 minutes in: the window closes there (no cleanup buffer), the rest is free.
        var reservation = await rig.Db.Reservations.AsNoTracking().SingleAsync();
        Assert.Equal(WorkStart.AddMinutes(30), reservation.EndDateTime);
        Assert.False(await rig.IsFreeAsync(WorkStart, WorkStart.AddMinutes(30)));
        Assert.True(await rig.IsFreeAsync(WorkStart.AddMinutes(30), WorkEnd));
    }

    [Fact]
    public async Task Edit_moves_the_mirror_reservation_and_a_blocked_edit_leaves_both_stores_untouched()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await rig.Service.CreateAsync(Request(rig, WorkStart, WorkEnd), default);
        var newStart = BrazilTimeZone.AtLocal(Day, new TimeOnly(12, 0));
        var newEnd = BrazilTimeZone.AtLocal(Day, new TimeOnly(13, 0));

        var moved = await rig.Service.UpdateAsync(
            created.Id,
            new UpdateAssetAllocationRequest { ExpectedVersion = created.Version, WorkStart = newStart, WorkEnd = newEnd },
            default);

        Assert.Equal(newStart, moved.OccupiedStart);
        var reservation = await rig.Db.Reservations.AsNoTracking().SingleAsync();
        Assert.Equal(newStart, reservation.StartDateTime);
        Assert.Equal(newEnd, reservation.EndDateTime);
        Assert.True(await rig.IsFreeAsync(WorkStart, WorkEnd));
        Assert.False(await rig.IsFreeAsync(newStart, newEnd));

        // A customer booking takes the afternoon; moving onto it is refused and nothing changes.
        var busyStart = BrazilTimeZone.AtLocal(Day, new TimeOnly(15, 0));
        var busyEnd = BrazilTimeZone.AtLocal(Day, new TimeOnly(16, 0));
        rig.AddReservation(ReservationPurpose.CustomerBooking, busyStart, busyEnd);
        await rig.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<PlanningException>(() => rig.Service.UpdateAsync(
            created.Id,
            new UpdateAssetAllocationRequest { ExpectedVersion = moved.Version, WorkStart = busyStart, WorkEnd = busyEnd },
            default));

        Assert.Equal(PlanningException.ConflictCode, ex.Code);
        var after = await rig.Db.Reservations.AsNoTracking().SingleAsync(r => r.Purpose == ReservationPurpose.InternalUse);
        Assert.Equal(newStart, after.StartDateTime);
        var allocation = await rig.Db.AssetAllocations.AsNoTracking().SingleAsync();
        Assert.Equal(newStart, allocation.OccupiedStart);
    }

    [Theory]
    [InlineData(10, 0, 11, 30)]  // 90 minutes is not an allowed duration
    [InlineData(6, 0, 7, 0)]     // before opening time
    [InlineData(21, 30, 22, 30)] // closes at 22:00
    public async Task The_rental_schedule_rules_apply_and_nothing_is_written_when_they_reject(
        int startHour, int startMinute, int endHour, int endMinute)
    {
        await using var rig = await Rig.CreateAsync();
        var start = BrazilTimeZone.AtLocal(Day, new TimeOnly(startHour, startMinute));
        var end = BrazilTimeZone.AtLocal(Day, new TimeOnly(endHour, endMinute));

        var preview = await rig.Service.PreviewAsync(Request(rig, start, end), default);
        Assert.False(preview.Feasible);
        Assert.Equal(PlanningException.UnsupportedWorkWindowReason, preview.HoursViolation);

        var ex = await Assert.ThrowsAsync<PlanningException>(
            () => rig.Service.CreateAsync(Request(rig, start, end), default));

        Assert.Equal(422, ex.StatusCode);
        Assert.Equal(PlanningException.UnsupportedWorkWindowCode, ex.Code);
        Assert.Equal(PlanningException.UnsupportedWorkWindowReason, ex.HoursViolation);
        Assert.Empty(await rig.Db.AssetAllocations.AsNoTracking().ToListAsync());
        Assert.Empty(await rig.Db.Reservations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_window_spanning_midnight_is_rejected_because_rentals_is_single_day()
    {
        await using var rig = await Rig.CreateAsync();
        var start = BrazilTimeZone.AtLocal(Day, new TimeOnly(21, 0));
        var end = BrazilTimeZone.AtLocal(Day.AddDays(1), new TimeOnly(9, 0));

        var ex = await Assert.ThrowsAsync<PlanningException>(
            () => rig.Service.CreateAsync(Request(rig, start, end), default));

        Assert.Equal(PlanningException.UnsupportedWorkWindowCode, ex.Code);
    }

    [Fact]
    public async Task A_bridged_allocation_never_expires_because_its_reservation_keeps_blocking()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await rig.Service.CreateAsync(Request(rig, WorkStart, WorkEnd), default);

        rig.Clock.SetUtcNow(WorkEnd.AddDays(1));
        var later = await rig.Service.GetAsync(created.Id, default);

        Assert.False(later.IsExpired);
        Assert.True(later.IsBlocking);
        Assert.False(await rig.IsFreeAsync(WorkStart, WorkEnd));

        // Cancelling it still frees both stores.
        await rig.Service.ApplyUsageAsync(created.Id, Usage("cancel", later.Version), default);
        await rig.AssertStoresAsync(AllocationStatus.Cancelled, ReservationStatus.Canceled);
        Assert.True(await rig.IsFreeAsync(WorkStart, WorkEnd));
    }

    [Fact]
    public async Task An_allocation_that_predates_the_bridge_still_blocks_and_is_named()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Db.AssetAllocations.Add(new AssetAllocation
        {
            TenantId = rig.Seed.TenantId,
            AssetId = rig.Seed.AssetId,
            UnitId = rig.Seed.UnitId,
            Title = "Legacy job",
            PlannedWorkStart = WorkStart,
            PlannedWorkEnd = WorkEnd,
            OccupiedStart = WorkStart,
            OccupiedEnd = WorkEnd,
            TimeZoneId = "America/Sao_Paulo",
        });
        await rig.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<PlanningException>(
            () => rig.Service.CreateAsync(Request(rig, WorkStart, WorkEnd), default));

        var conflict = Assert.Single(ex.Conflicts!);
        Assert.Equal("allocation", conflict.Source);
        Assert.Equal("Legacy job", conflict.Label);

        var elsewhere = BrazilTimeZone.AtLocal(Day, new TimeOnly(14, 0));
        var free = await rig.Service.CreateAsync(Request(rig, elsewhere, elsewhere.AddHours(1)), default);
        Assert.Equal("Scheduled", free.Status);
    }

    [Fact]
    public async Task Agenda_returns_customer_reservations_as_external_busy_and_not_the_allocation_mirror()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.Service.CreateAsync(Request(rig, WorkStart, WorkEnd), default);
        var from = BrazilTimeZone.AtLocal(Day, new TimeOnly(0, 0));
        var to = from.AddDays(1);
        // Spans the start of the range: clipped to it.
        rig.AddReservation(ReservationPurpose.CustomerBooking, from.AddHours(-1), from.AddHours(1));
        rig.AddReservation(ReservationPurpose.CustomerBooking, from.AddHours(14), from.AddHours(15));
        // An order allocated through the catalog has no allocation row: shown as busy too.
        rig.AddReservation(ReservationPurpose.InternalUse, from.AddHours(16), from.AddHours(17));
        // Outside the range: not returned.
        rig.AddReservation(ReservationPurpose.CustomerBooking, to.AddHours(2), to.AddHours(3));
        await rig.Db.SaveChangesAsync();

        var agenda = await rig.Agenda.GetAgendaAsync(from, to, null, null, default);

        var printer = agenda.Resources.Single(r => r.AssetId == rig.Seed.AssetId);
        Assert.True(printer.IsRentalBacked);
        Assert.False(agenda.Resources.Single(r => r.AssetId == rig.Seed.SecondAssetId).IsRentalBacked);
        var item = Assert.Single(agenda.Items);
        Assert.Equal(WorkStart, item.PlannedWorkStart);
        Assert.Equal(
            [(from, from.AddHours(1)), (from.AddHours(14), from.AddHours(15)), (from.AddHours(16), from.AddHours(17))],
            agenda.ExternalBusy.Select(b => (b.Start, b.End)).ToArray());
        Assert.All(agenda.ExternalBusy, b => Assert.Equal(rig.Seed.AssetId, b.AssetId));
    }

    [Fact]
    public async Task Another_tenants_reservations_neither_block_nor_show_up()
    {
        await using var rig = await Rig.CreateAsync(withOtherTenant: true);
        rig.AddReservation(
            ReservationPurpose.CustomerBooking, WorkStart, WorkEnd, rig.Other!.TenantId, rig.Other.UnitId, rig.OtherRentalAssetId);
        await rig.Db.SaveChangesAsync();
        var from = BrazilTimeZone.AtLocal(Day, new TimeOnly(0, 0));

        var created = await rig.Service.CreateAsync(Request(rig, WorkStart, WorkEnd), default);
        var agenda = await rig.Agenda.GetAgendaAsync(from, from.AddDays(1), null, null, default);
        var foreign = await rig.Rentals.ListBlockingWindowsAsync(
            rig.Db, rig.Seed.TenantId, [rig.OtherRentalAssetId], from, from.AddDays(1), default);

        Assert.Equal("Scheduled", created.Status);
        Assert.Empty(agenda.ExternalBusy);
        Assert.Empty(foreign);
    }

    private static CreateAssetAllocationRequest Request(
        Rig rig,
        DateTimeOffset start,
        DateTimeOffset end,
        string title = "Calibration",
        Guid? assetId = null) =>
        new()
        {
            AssetId = assetId ?? rig.Seed.AssetId,
            Kind = "Use",
            Title = title,
            WorkStart = start,
            WorkEnd = end,
        };

    private static AssetAllocationUsageRequest Usage(string action, string? version, string? reason = null) =>
        new() { Action = action, ExpectedVersion = version, Reason = reason };

    internal sealed class Rig : IAsyncDisposable
    {
        private static readonly DateTimeOffset Now = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);

        private Rig(
            AppDbContext db,
            PlanningTenantSeed seed,
            PlanningTenantSeed? other,
            Guid rentalAssetId,
            Guid otherRentalAssetId,
            TestTimeProvider clock)
        {
            Db = db;
            Seed = seed;
            Other = other;
            RentalAssetId = rentalAssetId;
            OtherRentalAssetId = otherRentalAssetId;
            Clock = clock;
            Rentals = new ResourceOccupancyService(clock);
            var accessor = new FixedModuleAccessor(PlatformModules.ResourcePlanning, PlatformModules.Rentals);
            var tenantProvider = new FakeTenantProvider { TenantId = seed.TenantId };
            var occupancy = new AssetOccupancyService(db, accessor, Rentals, clock);
            Service = new AssetAllocationService(
                db, tenantProvider, occupancy, new FakePlanningCaller(Creator, canManage: false), clock);
            Agenda = new PlanningAgendaService(db, tenantProvider, accessor, occupancy, clock);
        }

        public AppDbContext Db { get; }

        public PlanningTenantSeed Seed { get; }

        public PlanningTenantSeed? Other { get; }

        public Guid RentalAssetId { get; }

        public Guid OtherRentalAssetId { get; }

        public TestTimeProvider Clock { get; }

        public ResourceOccupancyService Rentals { get; }

        public AssetAllocationService Service { get; }

        public PlanningAgendaService Agenda { get; }

        public static async Task<Rig> CreateAsync(bool withOtherTenant = false)
        {
            var provider = new FakeTenantProvider();
            var db = InMemoryAppDb.Create(provider);
            var seed = PlanningTestData.AddTenant(db, withRentalsModule: true);
            var rentalAssetId = AddRental(db, seed.TenantId, seed.AssetId);

            PlanningTenantSeed? other = null;
            var otherRentalAssetId = Guid.Empty;
            if (withOtherTenant)
            {
                other = PlanningTestData.AddTenant(db, name: "Other Shop", withRentalsModule: true);
                otherRentalAssetId = AddRental(db, other.TenantId, other.AssetId);
            }

            provider.TenantId = seed.TenantId;
            await db.SaveChangesAsync();
            return new Rig(db, seed, other, rentalAssetId, otherRentalAssetId, new TestTimeProvider(Now));
        }

        public Reservation AddReservation(
            ReservationPurpose purpose,
            DateTimeOffset start,
            DateTimeOffset end,
            Guid? tenantId = null,
            Guid? unitId = null,
            Guid? rentalAssetId = null)
        {
            var tenant = tenantId ?? Seed.TenantId;
            var customer = purpose == ReservationPurpose.CustomerBooking;
            var reservation = new Reservation
            {
                TenantId = tenant,
                UnitId = unitId ?? Seed.UnitId,
                Purpose = purpose,
                CustomerName = customer ? "Maria" : null,
                CustomerWhatsApp = customer ? "5511999990000" : null,
                StartDateTime = start,
                EndDateTime = end,
                Status = ReservationStatus.Confirmed,
                TotalAmount = 0m,
                DepositPaid = 0m,
            };
            var item = new ReservationItem
            {
                TenantId = tenant,
                ReservationId = reservation.Id,
                RentalAssetId = rentalAssetId ?? RentalAssetId,
                Quantity = 1,
                UnitPrice = 0m,
                SubTotal = 0m,
            };
            reservation.AddItem(item);
            Db.Reservations.Add(reservation);
            Db.ReservationItems.Add(item);
            return reservation;
        }

        /// <summary>The same facts read from both stores: the allocation row and its mirror reservation.</summary>
        public async Task AssertStoresAsync(AllocationStatus allocation, ReservationStatus reservation)
        {
            var allocationRow = await Db.AssetAllocations.AsNoTracking().SingleAsync();
            Assert.Equal(allocation, allocationRow.Status);
            var item = await Db.ReservationItems.AsNoTracking().SingleAsync(i => i.Id == allocationRow.ReservationItemId);
            var reservationRow = await Db.Reservations.AsNoTracking().SingleAsync(r => r.Id == item.ReservationId);
            Assert.Equal(reservation, reservationRow.Status);
        }

        public async Task<bool> IsFreeAsync(DateTimeOffset start, DateTimeOffset end) =>
            (await Rentals.CheckAvailabilityAsync(Db, Seed.TenantId, RentalAssetId, start, end, [], default)).IsAvailable;

        public ValueTask DisposeAsync() => Db.DisposeAsync();

        private static Guid AddRental(AppDbContext db, Guid tenantId, Guid assetId)
        {
            var rental = new RentalAsset
            {
                TenantId = tenantId,
                AssetId = assetId,
                Type = RentalAssetType.Good,
                TotalQuantity = 1,
                IsActive = true,
                RequiresDeposit = false,
                SchedulePolicy = SchedulePolicy.OpenHours,
                OpenTime = new TimeOnly(8, 0),
                CloseTime = new TimeOnly(22, 0),
                AllowedDurationMinutes = "60,120",
                QueueEnabled = false,
            };
            db.RentalAssets.Add(rental);
            db.Assets.Local.Single(a => a.Id == assetId).IsRentable = true;
            return rental.Id;
        }
    }
}
