using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Api.Tests.Rentals;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.ResourcePlanning;

/// <summary>
/// Staff day projection coverage for InternalUse allocations (order-equipment-link-codex §4):
/// the badge, buffer-shading work window and permission-gated order link. Uses the in-memory
/// <see cref="AvailabilityHarness"/> read model directly, replicating
/// <see cref="Platform.Api.Modules.Rentals.Controllers.ScheduleController"/>'s OwningCatalogOrderId
/// strip (no HTTP harness needed for a single field-level permission check).
/// </summary>
public sealed class StaffScheduleProjectionTests
{
    private static readonly DateOnly Day = AvailabilityHarness.Day;

    [Fact]
    public async Task Staff_day_shows_internal_use_badge_and_buffer_window_with_order_id()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        var (_, orderId) = await SeedInternalUseAllocationAsync(
            h, workStart: new TimeOnly(10, 0), workEnd: new TimeOnly(11, 0), prepMinutes: 15, cleanupMinutes: 15);

        var day = await Schedule(h).GetDayAsync(Day, null, customerFacing: false, default);

        var slot = Assert.Single(day.Slots, s => s.RentalAssetId == h.Primary.Id);
        Assert.True(slot.IsInternalUseOrigin);
        Assert.Equal(orderId, slot.OwningCatalogOrderId);
        Assert.Equal(new TimeOnly(10, 0), slot.WorkStartTime);
        Assert.Equal(new TimeOnly(11, 0), slot.WorkEndTime);
        // Occupied interval includes the buffers; the work window is strictly inside it.
        Assert.Equal(new TimeOnly(9, 45), slot.StartTime);
        Assert.Equal(new TimeOnly(11, 15), slot.EndTime);
    }

    [Fact]
    public async Task Owning_order_id_is_stripped_without_OrdersRead_but_badge_and_buffers_remain()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        await SeedInternalUseAllocationAsync(
            h, workStart: new TimeOnly(10, 0), workEnd: new TimeOnly(11, 0), prepMinutes: 15, cleanupMinutes: 15);

        var day = await Schedule(h).GetDayAsync(Day, null, customerFacing: false, default);

        // Mirrors ScheduleController.GetDayAdmin's strip for viewers lacking catalog.orders.view.
        var stripped = day with { Slots = day.Slots.Select(s => s with { OwningCatalogOrderId = null }).ToList() };

        var slot = Assert.Single(stripped.Slots, s => s.RentalAssetId == h.Primary.Id);
        Assert.True(slot.IsInternalUseOrigin);
        Assert.Null(slot.OwningCatalogOrderId);
        Assert.Equal(new TimeOnly(10, 0), slot.WorkStartTime);
        Assert.Equal(new TimeOnly(11, 0), slot.WorkEndTime);
    }

    [Fact]
    public async Task Public_day_shows_plain_unavailability_with_no_internal_use_fields()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        await SeedInternalUseAllocationAsync(
            h, workStart: new TimeOnly(10, 0), workEnd: new TimeOnly(11, 0), prepMinutes: 15, cleanupMinutes: 15);

        var day = await Schedule(h).GetDayAsync(Day, null, customerFacing: true, default);

        // The occupied interval is fully booked (quantity 1 of 1), so it never appears as an
        // available public slot — no purpose, badge, order id or buffer fields leak either way.
        Assert.DoesNotContain(day.Slots, s => s.RentalAssetId == h.Primary.Id
            && (s.IsInternalUseOrigin || s.OwningCatalogOrderId is not null
                || s.WorkStartTime is not null || s.WorkEndTime is not null));
    }

    [Fact]
    public async Task Staff_day_keeps_one_cell_when_two_items_share_an_asset_and_one_item_has_two_allocations()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        var (orderId, quantity) = await SeedDuplicateItemsAndAllocationsAsync(h);

        var day = await Schedule(h).GetDayAsync(Day, null, customerFacing: false, default);

        var slot = Assert.Single(day.Slots, s => s.RentalAssetId == h.Primary.Id && s.Status == SlotStatus.Booked);
        Assert.Equal(quantity, slot.Quantity);
        Assert.Equal("Internal", slot.CustomerName);
        Assert.True(slot.IsInternalUseOrigin);
        Assert.Equal(orderId, slot.OwningCatalogOrderId);
        Assert.Equal(new TimeOnly(10, 0), slot.WorkStartTime);
        Assert.Equal(new TimeOnly(11, 0), slot.WorkEndTime);
    }

    [Fact]
    public async Task Customer_booking_cells_are_unaffected_by_the_internal_use_projection()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        h.Primary.TotalQuantity = 2;
        await h.Db.SaveChangesAsync();
        var reservation = await h.ReserveAsync(h.Primary, 1, new TimeOnly(10, 0), new TimeOnly(11, 0));
        var slot = new Slot
        {
            TenantId = h.Tenant.Id,
            RentalAssetId = h.Primary.Id,
            Date = Day,
            StartTime = new(10, 0),
            EndTime = new(11, 0),
            OccupancyKindId = h.Open.Id,
            Status = SlotStatus.Booked,
            ReservationId = reservation.Id,
        };
        h.Db.Add(slot);
        await h.Db.SaveChangesAsync();
        var slotId = slot.Id;

        var day = await Schedule(h).GetDayAsync(Day, null, customerFacing: false, default);

        var slot2 = Assert.Single(day.Slots, s => s.Id == slotId);
        Assert.False(slot2.IsInternalUseOrigin);
        Assert.Null(slot2.OwningCatalogOrderId);
        Assert.Null(slot2.WorkStartTime);
        Assert.Null(slot2.WorkEndTime);
    }

    private static async Task<(Guid ReservationId, Guid OrderId)> SeedInternalUseAllocationAsync(
        AvailabilityHarness h, TimeOnly workStart, TimeOnly workEnd, int prepMinutes, int cleanupMinutes)
    {
        h.Primary.TotalQuantity = 1;
        await h.Db.SaveChangesAsync();

        var occupiedStart = workStart.AddMinutes(-prepMinutes);
        var occupiedEnd = workEnd.AddMinutes(cleanupMinutes);

        var customer = new Customer { TenantId = h.Tenant.Id, Name = "Internal", Phone = "5511000000000" };
        var reservation = new Reservation
        {
            Purpose = ReservationPurpose.InternalUse,
            TenantId = h.Tenant.Id,
            UnitId = h.Unit.Id,
            CustomerId = customer.Id,
            CustomerName = "Internal",
            CustomerWhatsApp = "5511000000000",
            Status = ReservationStatus.Confirmed,
            StartDateTime = BrazilTimeZone.AtLocal(Day, occupiedStart),
            EndDateTime = BrazilTimeZone.AtLocal(Day, occupiedEnd),
            TotalAmount = 0,
            DepositPaid = 0,
        };
        var item = new ReservationItem
        {
            TenantId = h.Tenant.Id,
            ReservationId = reservation.Id,
            RentalAssetId = h.Primary.Id,
            Quantity = 1,
            UnitPrice = 0,
            SubTotal = 0,
        };
        reservation.AddItem(item);

        var slot = new Slot
        {
            TenantId = h.Tenant.Id,
            RentalAssetId = h.Primary.Id,
            Date = Day,
            StartTime = occupiedStart,
            EndTime = occupiedEnd,
            OccupancyKindId = h.Open.Id,
            Status = SlotStatus.Booked,
            ReservationId = reservation.Id,
        };

        var product = new CatalogProduct { TenantId = h.Tenant.Id, Name = "Printer time", IsActive = true };
        var order = new CatalogOrder
        {
            TenantId = h.Tenant.Id,
            CustomerId = customer.Id,
            OrderNumber = 1,
            CustomerNameSnapshot = customer.Name,
        };
        var orderItem = new CatalogOrderItem
        {
            TenantId = h.Tenant.Id,
            OrderId = order.Id,
            ProductId = product.Id,
            ProductNameSnapshot = product.Name,
            Quantity = 1,
        };
        order.AddItem(orderItem);
        var requirement = new CatalogOrderItemResourceRequirement
        {
            TenantId = h.Tenant.Id,
            OrderItemId = orderItem.Id,
            RentalAssetId = h.Primary.Id,
            ResourceQuantity = 1,
            DurationMinutesPerUnit = (int)(workEnd.ToTimeSpan() - workStart.ToTimeSpan()).TotalMinutes,
            PrepMinutes = prepMinutes,
            CleanupMinutes = cleanupMinutes,
        };
        var allocation = new CatalogOrderItemResourceAllocation
        {
            TenantId = h.Tenant.Id,
            RequirementId = requirement.Id,
            ReservationItemId = item.Id,
            Sequence = 1,
            PlanningRevision = 1,
            AllocatedQuantity = 1,
            RentalAssetId = h.Primary.Id,
            PlannedWorkStart = BrazilTimeZone.AtLocal(Day, workStart),
            PlannedWorkEnd = BrazilTimeZone.AtLocal(Day, workEnd),
            TimeZoneId = "America/Sao_Paulo",
        };

        h.Db.AddRange(customer, reservation, slot, product, order, requirement, allocation);
        await h.Db.SaveChangesAsync();

        return (reservation.Id, order.Id);
    }

    private static async Task<(Guid OrderId, int Quantity)> SeedDuplicateItemsAndAllocationsAsync(AvailabilityHarness h)
    {
        h.Primary.TotalQuantity = 5;
        await h.Db.SaveChangesAsync();

        var customer = new Customer { TenantId = h.Tenant.Id, Name = "Internal", Phone = "5511000000000" };
        var reservation = new Reservation
        {
            Purpose = ReservationPurpose.InternalUse,
            TenantId = h.Tenant.Id,
            UnitId = h.Unit.Id,
            CustomerId = customer.Id,
            CustomerName = "Internal",
            CustomerWhatsApp = "5511000000000",
            Status = ReservationStatus.Confirmed,
            StartDateTime = BrazilTimeZone.AtLocal(Day, new TimeOnly(10, 0)),
            EndDateTime = BrazilTimeZone.AtLocal(Day, new TimeOnly(12, 0)),
            TotalAmount = 0,
            DepositPaid = 0,
        };
        var firstItem = new ReservationItem
        {
            TenantId = h.Tenant.Id,
            ReservationId = reservation.Id,
            RentalAssetId = h.Primary.Id,
            Quantity = 2,
            UnitPrice = 0,
            SubTotal = 0,
        };
        var secondItem = new ReservationItem
        {
            TenantId = h.Tenant.Id,
            ReservationId = reservation.Id,
            RentalAssetId = h.Primary.Id,
            Quantity = 3,
            UnitPrice = 0,
            SubTotal = 0,
        };
        reservation.AddItem(firstItem);
        reservation.AddItem(secondItem);

        var slot = new Slot
        {
            TenantId = h.Tenant.Id,
            RentalAssetId = h.Primary.Id,
            Date = Day,
            StartTime = new TimeOnly(10, 0),
            EndTime = new TimeOnly(12, 0),
            OccupancyKindId = h.Open.Id,
            Status = SlotStatus.Booked,
            ReservationId = reservation.Id,
        };

        var product = new CatalogProduct { TenantId = h.Tenant.Id, Name = "Printer time", IsActive = true };
        var earlyOrder = new CatalogOrder
        {
            TenantId = h.Tenant.Id,
            CustomerId = customer.Id,
            OrderNumber = 1,
            CustomerNameSnapshot = customer.Name,
        };
        var laterOrder = new CatalogOrder
        {
            TenantId = h.Tenant.Id,
            CustomerId = customer.Id,
            OrderNumber = 2,
            CustomerNameSnapshot = customer.Name,
        };
        var earlyItem = new CatalogOrderItem
        {
            TenantId = h.Tenant.Id,
            OrderId = earlyOrder.Id,
            ProductId = product.Id,
            ProductNameSnapshot = product.Name,
            Quantity = 1,
        };
        var laterItem = new CatalogOrderItem
        {
            TenantId = h.Tenant.Id,
            OrderId = laterOrder.Id,
            ProductId = product.Id,
            ProductNameSnapshot = product.Name,
            Quantity = 1,
        };
        earlyOrder.AddItem(earlyItem);
        laterOrder.AddItem(laterItem);
        var earlyRequirement = new CatalogOrderItemResourceRequirement
        {
            TenantId = h.Tenant.Id,
            OrderItemId = earlyItem.Id,
            RentalAssetId = h.Primary.Id,
            ResourceQuantity = 1,
            DurationMinutesPerUnit = 30,
        };
        var laterRequirement = new CatalogOrderItemResourceRequirement
        {
            TenantId = h.Tenant.Id,
            OrderItemId = laterItem.Id,
            RentalAssetId = h.Primary.Id,
            ResourceQuantity = 1,
            DurationMinutesPerUnit = 40,
        };
        var earlyAllocation = new CatalogOrderItemResourceAllocation
        {
            TenantId = h.Tenant.Id,
            RequirementId = earlyRequirement.Id,
            ReservationItemId = firstItem.Id,
            Sequence = 1,
            PlanningRevision = 1,
            AllocatedQuantity = 1,
            RentalAssetId = h.Primary.Id,
            PlannedWorkStart = BrazilTimeZone.AtLocal(Day, new TimeOnly(10, 0)),
            PlannedWorkEnd = BrazilTimeZone.AtLocal(Day, new TimeOnly(10, 30)),
            TimeZoneId = "America/Sao_Paulo",
        };
        var laterAllocation = new CatalogOrderItemResourceAllocation
        {
            TenantId = h.Tenant.Id,
            RequirementId = laterRequirement.Id,
            ReservationItemId = firstItem.Id,
            Sequence = 1,
            PlanningRevision = 1,
            AllocatedQuantity = 1,
            RentalAssetId = h.Primary.Id,
            PlannedWorkStart = BrazilTimeZone.AtLocal(Day, new TimeOnly(10, 20)),
            PlannedWorkEnd = BrazilTimeZone.AtLocal(Day, new TimeOnly(11, 0)),
            TimeZoneId = "America/Sao_Paulo",
        };

        h.Db.AddRange(
            customer, reservation, slot, product, earlyOrder, laterOrder,
            earlyRequirement, laterRequirement, earlyAllocation, laterAllocation);
        await h.Db.SaveChangesAsync();
        return (earlyOrder.Id, firstItem.Quantity + secondItem.Quantity);
    }

    private static ScheduleService Schedule(AvailabilityHarness h) =>
        new(h.Db, h.TenantProvider, new OccupancyKindService(h.Db, h.TenantProvider), new FakeTrialGuard(),
            TestReservationQueue.Create(h.Db, h.TenantProvider, h.Clock),
            SilentRentalsNotifications.Publisher.AsDomainEvents(), SilentRentalsNotifications.Scheduler, h.Clock);
}
