using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.Rentals;

/// <summary>
/// Completing a reservation never releases its time, and windows whose start already passed
/// are never offered to customers (public schedule day and public availability).
/// </summary>
public sealed class CompletedAndPastSlotAvailabilityTests
{
    private static readonly DateOnly Day = AvailabilityHarness.Day;

    [Fact]
    public async Task Completed_reservation_keeps_its_slot_and_derived_window_taken_with_zero_available()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        h.Primary.Type = RentalAssetType.Location;
        h.Primary.TotalQuantity = 1;
        var onSlot = Reserve(h, h.Primary, ReservationStatus.Confirmed, 10, 11);
        var onDerived = Reserve(h, h.Primary, ReservationStatus.Confirmed, 11, 12);
        var slot = h.Slot(10, 11, SlotStatus.Booked);
        slot.ReservationId = onSlot.Id;
        h.Db.Slots.Add(slot);
        await h.Db.SaveChangesAsync();

        var reservations = Reservations(h);
        await reservations.CompleteAsync(onSlot.Id, default);
        await reservations.CompleteAsync(onDerived.Id, default);

        var windows = Assert.Single((await h.ReadAsync()).Resources).Windows;
        Assert.All(windows, w =>
        {
            Assert.Equal(AvailabilityWindowState.Taken, w.State);
            Assert.Equal(0, w.AvailableQuantity);
        });
        Assert.Equal(SlotStatus.Booked, (await h.Db.Slots.AsNoTracking().SingleAsync()).Status);
        Assert.Empty((await Schedule(h).GetDayAsync(Day, null, customerFacing: true, default)).Slots);
        var check = await reservations.CheckAvailabilityAsync(new CheckAvailabilityRequestDto
        {
            AssetId = h.Primary.AssetId, Date = Day, StartTime = new(11, 0), EndTime = new(12, 0), Quantity = 1,
        }, default);
        Assert.False(check.IsAvailable);
        Assert.Equal(0, check.AvailableQuantity);
    }

    [Fact]
    public async Task Past_windows_are_never_available_to_customers_while_staff_still_sees_them()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        h.Primary.OpenTime = new(9, 0);
        h.Db.Slots.Add(h.Slot(10, 11, SlotStatus.Available));
        await h.Db.SaveChangesAsync();
        // 09:00 (derived) and 10:00 (persisted) already started; 11:00 starts exactly now and stays bookable.
        h.Clock.SetUtcNow(BrazilTimeZone.AtLocal(Day, new(11, 0)));

        var windows = Assert.Single((await h.ReadAsync()).Resources).Windows;
        Assert.Equal([new(9, 0), new(10, 0), new TimeOnly(11, 0)], windows.Select(w => w.StartTime));
        Assert.All(windows.Take(2), w =>
        {
            Assert.Equal(AvailabilityWindowState.Past, w.State);
            Assert.Equal(0, w.AvailableQuantity);
        });
        Assert.Equal(AvailabilityWindowState.Available, windows[2].State);
        Assert.Equal(5, windows[2].AvailableQuantity);

        var publicDay = await Schedule(h).GetDayAsync(Day, null, customerFacing: true, default);
        Assert.Equal([new TimeOnly(11, 0)], publicDay.Slots.Select(s => s.StartTime));
        var adminDay = await Schedule(h).GetDayAsync(Day, null, customerFacing: false, default);
        Assert.Equal([new(9, 0), new(10, 0), new TimeOnly(11, 0)], adminDay.Slots.Select(s => s.StartTime));
    }

    [Fact]
    public async Task Future_windows_are_unchanged()
    {
        await using var h = await AvailabilityHarness.CreateAsync();

        var windows = Assert.Single((await h.ReadAsync()).Resources).Windows;
        Assert.All(windows, w =>
        {
            Assert.Equal(AvailabilityWindowState.Available, w.State);
            Assert.Equal(5, w.AvailableQuantity);
        });
        var publicDay = await Schedule(h).GetDayAsync(Day, null, customerFacing: true, default);
        Assert.Equal([new(10, 0), new TimeOnly(11, 0)], publicDay.Slots.Select(s => s.StartTime));
    }

    [Fact]
    public async Task Completed_reservation_of_another_tenant_does_not_hold_capacity()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        var other = new Tenant("Other", "22222222000191", subdomain: "other");
        var otherUnit = new Unit(other.Id, "Other main");
        h.Db.AddRange(other, otherUnit, new OccupancyKind { TenantId = other.Id, Key = "open", Label = "Open",
            IsActive = true, IsBookableByCustomer = true, BlocksCapacity = false, SortOrder = 0 });
        var otherResource = await h.AddResourceAsync(other, otherUnit, "Other resource");
        otherResource.Type = RentalAssetType.Location;
        Reserve(h, otherResource, ReservationStatus.Completed, 10, 11);
        await h.Db.SaveChangesAsync();

        var mine = Assert.Single((await h.ReadAsync()).Resources).Windows;
        Assert.All(mine, w => Assert.Equal(5, w.AvailableQuantity));
        Assert.Equal(2, (await Schedule(h).GetDayAsync(Day, null, customerFacing: true, default)).Slots.Count);

        h.TenantProvider.TenantId = other.Id;
        var theirs = Assert.Single((await h.ReadAsync()).Resources).Windows;
        Assert.Equal(0, theirs.Single(w => w.StartTime == new TimeOnly(10, 0)).AvailableQuantity);
    }

    private static Reservation Reserve(AvailabilityHarness h, RentalAsset resource, ReservationStatus status,
        int startHour, int endHour)
    {
        var customer = new Customer { TenantId = resource.TenantId, Name = "Customer", Phone = "5511000000000" };
        var reservation = new Reservation { TenantId = resource.TenantId, UnitId = h.Db.Assets.Local
                .Single(a => a.Id == resource.AssetId).UnitId, CustomerId = customer.Id,
            CustomerName = "Customer", CustomerWhatsApp = "5511000000000", Status = status,
            StartDateTime = BrazilTimeZone.AtLocal(Day, new(startHour, 0)),
            EndDateTime = BrazilTimeZone.AtLocal(Day, new(endHour, 0)), TotalAmount = 20, DepositPaid = 0 };
        reservation.AddItem(new ReservationItem { TenantId = resource.TenantId, ReservationId = reservation.Id,
            RentalAssetId = resource.Id, Quantity = 1, UnitPrice = 20, SubTotal = 20 });
        h.Db.AddRange(customer, reservation);
        return reservation;
    }

    private static ScheduleService Schedule(AvailabilityHarness h) =>
        new(h.Db, h.TenantProvider, new OccupancyKindService(h.Db, h.TenantProvider), new FakeTrialGuard(),
            TestReservationQueue.Create(h.Db, h.TenantProvider, h.Clock),
            SilentRentalsNotifications.Publisher.AsDomainEvents(), SilentRentalsNotifications.Scheduler, h.Clock);

    private static ReservationService Reservations(AvailabilityHarness h) =>
        new(h.Db, h.TenantProvider, new FakeTrialGuard(), TestReservationQueue.Create(h.Db, h.TenantProvider, h.Clock),
            SilentRentalsNotifications.Publisher.AsDomainEvents(), SilentRentalsNotifications.Scheduler, h.Clock);
}
