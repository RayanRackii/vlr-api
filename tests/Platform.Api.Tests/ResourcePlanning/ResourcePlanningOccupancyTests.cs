using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Rentals.Contracts;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.ResourcePlanning;

public sealed partial class ResourcePlanningConcurrencyTests
{
    [DockerFact]
    public async Task Sequential_future_internal_use_allocations_both_approve()
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant);
        await using var db = factory.Create(tenant);
        var secondOrder = new CatalogOrder
        {
            TenantId = seed.TenantId, CustomerId = seed.CustomerId, OrderNumber = 2,
            CustomerNameSnapshot = "Ana", Currency = "BRL",
        };
        var item = new CatalogOrderItem
        {
            TenantId = seed.TenantId, OrderId = secondOrder.Id, ProductId = seed.ProductId,
            ProductNameSnapshot = "Impressao 3D", Quantity = 1, UnitPriceSnapshot = 50m,
            SubTotal = 50m, Currency = "BRL",
        };
        secondOrder.AddItem(item);
        db.CatalogOrders.Add(secondOrder);
        db.CatalogOrderItemResourceRequirements.Add(new CatalogOrderItemResourceRequirement
        {
            TenantId = seed.TenantId, OrderItemId = item.Id, RentalAssetId = seed.RentalAssetId,
            ResourceQuantity = 1, DurationMinutesPerUnit = 60,
        });
        await db.SaveChangesAsync();
        var orders = CreateOrders(db, tenant);

        var first = await orders.ApproveAsync(seed.OrderId, new CatalogOrderApprovePlanningRequest
        {
            UnitId = seed.UnitId, WorkStart = WorkStart, ExpectedVersion = 0,
        }, CancellationToken.None);
        var second = await orders.ApproveAsync(secondOrder.Id, new CatalogOrderApprovePlanningRequest
        {
            UnitId = seed.UnitId, WorkStart = WorkStart.AddHours(2), ExpectedVersion = 0,
        }, CancellationToken.None);

        Assert.Equal(CatalogOrderStatus.Approved, first.Status);
        Assert.Equal(CatalogOrderStatus.Approved, second.Status);
        var daily = await SeedAndReadPlanningDayAsync(db, tenant, seed, BookingClock());
        Assert.Equal(AvailabilityWindowState.Available,
            daily.Windows.Single(w => w.StartTime == new TimeOnly(11, 0)).State);
        Assert.Equal(AvailabilityWindowState.Taken,
            daily.Windows.Single(w => w.StartTime == new TimeOnly(12, 0)).State);
    }

    [DockerFact]
    public async Task Overdue_unreleased_internal_use_blocks_booking_and_daily_availability()
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant);
        await using var db = factory.Create(tenant);
        var clock = BookingClock();
        var occupancy = new ResourceOccupancyService(clock);
        await occupancy.CreateInternalUseOccupancyAsync(db, seed.TenantId, seed.UnitId,
            seed.RentalAssetId, 1, WorkStart.AddDays(-1), WorkEnd.AddDays(-1), CancellationToken.None);
        await db.SaveChangesAsync();
        var daily = await SeedAndReadPlanningDayAsync(db, tenant, seed, clock);
        var window = daily.Windows.Single(w => w.StartTime == WorkStartLocal);
        Assert.Equal(AvailabilityWindowState.Taken, window.State);
        Assert.Equal(0, window.AvailableQuantity);

        var reservations = new ReservationService(db, tenant, new FakeTrialGuard(),
            TestReservationQueue.Create(db, tenant),
            SilentRentalsNotifications.Publisher.AsDomainEvents(), SilentRentalsNotifications.Scheduler, clock);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => reservations.CreateReservationAsync(
            seed.CustomerId, new CreateReservationRequestDto
            {
                UnitId = seed.UnitId, Date = Date, StartTime = WorkStartLocal, EndTime = WorkEndLocal,
                Items = [new CreateReservationItemRequestDto { AssetId = seed.AssetId, Quantity = 1 }],
            }, CancellationToken.None));
        Assert.Contains("não está disponível", error.Message);
    }

    [DockerFact]
    public async Task Late_internal_use_release_preserves_overrun_history_and_frees_later_times()
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant);
        var clock = new TestTimeProvider(WorkEnd);
        var occupancy = new ResourceOccupancyService(clock);
        await using (var db = factory.Create(tenant))
        {
            var itemId = await occupancy.CreateInternalUseOccupancyAsync(db, seed.TenantId, seed.UnitId,
                seed.RentalAssetId, 1, WorkStart, WorkEnd, CancellationToken.None);
            await db.SaveChangesAsync();
            // End == now is already overdue, including the half-open planned-end boundary.
            Assert.False((await occupancy.CheckAvailabilityAsync(db, seed.TenantId, seed.RentalAssetId,
                WorkEnd, WorkEnd.AddHours(1), [], CancellationToken.None)).IsAvailable);
            clock.Advance(TimeSpan.FromHours(2));
            await occupancy.ReleaseInternalUseOccupancyAsync(db, seed.TenantId, itemId,
                InternalUseReleaseKind.ReleaseAfterStart, WorkEnd, CancellationToken.None);
            await db.SaveChangesAsync();
            // A retry must not extend a reservation that has already been released.
            clock.Advance(TimeSpan.FromHours(1));
            await occupancy.ReleaseInternalUseOccupancyAsync(db, seed.TenantId, itemId,
                InternalUseReleaseKind.ReleaseAfterStart, WorkEnd, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using var verify = factory.Create(tenant);
        Assert.False((await occupancy.CheckAvailabilityAsync(verify, seed.TenantId, seed.RentalAssetId,
            WorkEnd.AddHours(1), WorkEnd.AddHours(2), [], CancellationToken.None)).IsAvailable);
        Assert.True((await occupancy.CheckAvailabilityAsync(verify, seed.TenantId, seed.RentalAssetId,
            WorkEnd.AddHours(2), WorkEnd.AddHours(3), [], CancellationToken.None)).IsAvailable);
        // Read the historical day with an earlier clock so Past does not mask capacity assertions.
        var daily = await SeedAndReadPlanningDayAsync(verify, tenant, seed, BookingClock());
        Assert.Equal(AvailabilityWindowState.Taken,
            daily.Windows.Single(w => w.StartTime == new TimeOnly(12, 0)).State);
        Assert.Equal(AvailabilityWindowState.Available,
            daily.Windows.Single(w => w.StartTime == new TimeOnly(13, 0)).State);
    }

    private static async Task<ResourceAvailabilityDto> SeedAndReadPlanningDayAsync(
        AppDbContext db, FakeTenantProvider tenant, Seed seed, TimeProvider clock)
    {
        db.OccupancyKinds.Add(new OccupancyKind
        {
            TenantId = seed.TenantId, Key = "open", Label = "Open", IsActive = true,
            IsBookableByCustomer = true, BlocksCapacity = false, SortOrder = 0,
        });
        db.RentalPricings.Add(new RentalPricing
        {
            TenantId = seed.TenantId, RentalAssetId = seed.RentalAssetId, DayOfWeek = Date.DayOfWeek,
            StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(22, 0), PricePerHour = 50m,
            RequiresDeposit = false, DepositPercentage = 0,
        });
        await db.SaveChangesAsync();
        var day = await new DailyAvailabilityService(db, tenant, clock)
            .GetAsync(Date, [seed.RentalAssetId], seed.UnitId, 1, 60, CancellationToken.None);
        return Assert.Single(day.Resources);
    }
}
