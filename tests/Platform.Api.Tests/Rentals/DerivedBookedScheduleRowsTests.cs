using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Rentals;

public sealed class DerivedBookedScheduleRowsTests
{
    [Fact]
    public async Task OpenHours_staff_day_includes_confirmed_reservation_and_public_day_is_unchanged()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        h.Primary.TotalQuantity = 2;
        h.Primary.OpenTime = new TimeOnly(8, 0);
        h.Primary.CloseTime = new TimeOnly(10, 0);
        var reservation = await h.ReserveAsync(h.Primary, 1, new(8, 0), new(10, 0));

        var schedule = CreateSchedule(h);
        var staffDay = await schedule.GetDayAsync(AvailabilityHarness.Day, h.Primary.Id, customerFacing: false, default);
        var booked = Assert.Single(staffDay.Slots, slot => slot.Status == SlotStatus.Booked);

        Assert.Equal(reservation.Id, booked.ReservationId);
        Assert.Equal(new TimeOnly(8, 0), booked.StartTime);
        Assert.Equal(new TimeOnly(10, 0), booked.EndTime);
        Assert.True(booked.IsDerived);
        Assert.Equal("open", booked.OccupancyKindKey);
        Assert.False(booked.IsBookableByCustomer);
        var customerSlots = (await schedule.GetDayAsync(
            AvailabilityHarness.Day, h.Primary.Id, customerFacing: true, default)).Slots;
        Assert.Equal(new[] { new TimeOnly(8, 0), new TimeOnly(9, 0) },
            customerSlots.Select(slot => slot.StartTime));
        Assert.All(customerSlots, slot => Assert.Equal(SlotStatus.Available, slot.Status));
    }

    [Fact]
    public async Task OpenHours_fully_booked_goods_include_each_booking_without_available_window()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        h.Primary.TotalQuantity = 3;
        h.Primary.OpenTime = new TimeOnly(8, 0);
        h.Primary.CloseTime = new TimeOnly(10, 0);
        var reservations = new[]
        {
            await h.ReserveAsync(h.Primary, 1, new(8, 0), new(10, 0)),
            await h.ReserveAsync(h.Primary, 1, new(8, 0), new(10, 0)),
            await h.ReserveAsync(h.Primary, 1, new(8, 0), new(10, 0)),
        };

        var day = await CreateSchedule(h).GetDayAsync(
            AvailabilityHarness.Day, h.Primary.Id, customerFacing: false, default);

        Assert.Equal(reservations.Select(reservation => reservation.Id).Order(),
            day.Slots.Where(slot => slot.Status == SlotStatus.Booked)
                .Select(slot => slot.ReservationId!.Value).Order());
        Assert.DoesNotContain(day.Slots, slot =>
            slot.Status == SlotStatus.Available
            && slot.StartTime < new TimeOnly(10, 0)
            && slot.EndTime > new TimeOnly(8, 0));
    }

    [Fact]
    public async Task OpenHours_cancelled_reservation_does_not_add_derived_booked_row()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        h.Primary.OpenTime = new TimeOnly(8, 0);
        h.Primary.CloseTime = new TimeOnly(10, 0);
        await h.ReserveAsync(
            h.Primary, 1, new(8, 0), new(10, 0), ReservationStatus.Canceled);

        var day = await CreateSchedule(h).GetDayAsync(
            AvailabilityHarness.Day, h.Primary.Id, customerFacing: false, default);

        Assert.DoesNotContain(day.Slots, slot => slot.Status == SlotStatus.Booked);
    }

    [Fact]
    public async Task Persisted_booked_slot_is_not_duplicated_by_its_reservation()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        h.Primary.TotalQuantity = 1;
        h.Primary.OpenTime = new TimeOnly(8, 0);
        h.Primary.CloseTime = new TimeOnly(10, 0);
        var reservation = await h.ReserveAsync(h.Primary, 1, new(8, 0), new(10, 0));
        var persisted = h.Slot(8, 10, SlotStatus.Booked);
        persisted.MarkBooked(reservation.Id);
        h.Db.Slots.Add(persisted);
        await h.Db.SaveChangesAsync();

        var booked = (await CreateSchedule(h).GetDayAsync(
            AvailabilityHarness.Day, h.Primary.Id, customerFacing: false, default)).Slots
            .Where(slot => slot.Status == SlotStatus.Booked)
            .ToList();

        var slot = Assert.Single(booked);
        Assert.Equal(persisted.Id, slot.Id);
        Assert.False(slot.IsDerived);
    }

    [Fact]
    public async Task Unpublished_slot_grid_staff_day_includes_confirmed_reservation()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        h.Primary.SchedulePolicy = SchedulePolicy.SlotGrid;
        var template = h.Template(8, 10, h.Open);
        h.Db.ScheduleTemplates.Add(template);
        var reservation = await h.ReserveAsync(h.Primary, 1, new(8, 0), new(10, 0));

        var day = await CreateSchedule(h).GetDayAsync(
            AvailabilityHarness.Day, h.Primary.Id, customerFacing: false, default);

        var booked = Assert.Single(day.Slots, slot => slot.Status == SlotStatus.Booked);
        Assert.Equal(reservation.Id, booked.ReservationId);
        Assert.Equal("open", booked.OccupancyKindKey);
        Assert.True(booked.IsDerived);
    }

    private static ScheduleService CreateSchedule(AvailabilityHarness h) =>
        new(
            h.Db,
            h.TenantProvider,
            new OccupancyKindService(h.Db, h.TenantProvider),
            new FakeTrialGuard(),
            TestReservationQueue.Create(h.Db, h.TenantProvider),
            SilentRentalsNotifications.Publisher.AsDomainEvents(),
            SilentRentalsNotifications.Scheduler,
            h.Clock);
}
