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

namespace Platform.Api.Tests.ResourcePlanning;

/// <summary>
/// Real-PostgreSQL coverage of the rentals bridge (spec slice API-4). The in-memory provider ignores
/// <c>SELECT ... FOR UPDATE</c> and the advisory locks, so the serialization of a manual allocation
/// on a rental-backed asset against a customer booking, and against another allocation, is proven
/// here: asset row, then rental-asset row, the same rental lock a customer booking takes.
/// </summary>
public sealed partial class ResourcePlanningConcurrencyTests
{
    [DockerFact]
    public async Task Manual_allocation_vs_customer_booking_on_a_rental_backed_asset_allows_only_one_winner()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenantProvider);
        await MakePlannableAsync(factory, tenantProvider, seed);

        await using var dbBooking = factory.Create(tenantProvider);
        await using var dbAllocate = factory.Create(tenantProvider);
        var reservationService = new ReservationService(
            dbBooking,
            tenantProvider,
            new FakePlanGuard(),
            TestReservationQueue.Create(dbBooking, tenantProvider),
            SilentRentalsNotifications.Publisher.AsDomainEvents(),
            SilentRentalsNotifications.Scheduler,
            BookingClock());
        var allocations = CreateAllocations(dbAllocate, tenantProvider);

        var bookingRequest = new Platform.Api.Modules.Rentals.Dtos.CreateReservationRequestDto
        {
            UnitId = seed.UnitId,
            Date = Date,
            StartTime = WorkStartLocal,
            EndTime = WorkEndLocal,
            Items = [new Platform.Api.Modules.Rentals.Dtos.CreateReservationItemRequestDto { AssetId = seed.AssetId, Quantity = 1 }],
        };
        var allocationRequest = new CreateAssetAllocationRequest
        {
            AssetId = seed.AssetId,
            Title = "Calibration",
            WorkStart = WorkStart,
            WorkEnd = WorkEnd,
        };

        var bookingTask = CaptureAsync(() => reservationService.CreateReservationAsync(seed.CustomerId, bookingRequest, CancellationToken.None));
        var allocationTask = CaptureAsync(() => allocations.CreateAsync(allocationRequest, CancellationToken.None));
        await Task.WhenAll(bookingTask, allocationTask);
        var bookingResult = await bookingTask;
        var allocationResult = await allocationTask;

        var errors = new[] { bookingResult.Error, allocationResult.Error };
        Assert.Single(errors, e => e is null);
        Assert.Single(errors, e => e is not null);

        // Whoever won, the calendar holds exactly one reservation item, and the allocation store
        // agrees: a winning allocation owns that item, a losing one left no row behind.
        await using var verify = factory.Create(tenantProvider);
        Assert.Equal(1, await verify.ReservationItems.CountAsync(i => i.RentalAssetId == seed.RentalAssetId));
        var allocationRows = await verify.AssetAllocations.Where(a => a.AssetId == seed.AssetId).ToListAsync();
        if (allocationResult.Error is null)
        {
            var row = Assert.Single(allocationRows);
            Assert.NotNull(row.ReservationItemId);
        }
        else
        {
            Assert.Empty(allocationRows);
        }
    }

    [DockerFact]
    public async Task Two_parallel_manual_allocations_on_a_rental_backed_asset_let_exactly_one_win_and_write_one_reservation()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenantProvider);
        await MakePlannableAsync(factory, tenantProvider, seed);

        await using var db1 = factory.Create(tenantProvider);
        await using var db2 = factory.Create(tenantProvider);
        var first = CreateAllocations(db1, tenantProvider);
        var second = CreateAllocations(db2, tenantProvider);
        CreateAssetAllocationRequest Request(string title) => new()
        {
            AssetId = seed.AssetId,
            Title = title,
            WorkStart = WorkStart,
            WorkEnd = WorkEnd,
        };

        var results = await Task.WhenAll(
            CaptureAsync(() => first.CreateAsync(Request("A"), CancellationToken.None)),
            CaptureAsync(() => second.CreateAsync(Request("B"), CancellationToken.None)));

        Assert.Single(results, r => r.Error is null);
        var loser = Assert.Single(results, r => r.Error is not null);
        var ex = Assert.IsType<PlanningException>(loser.Error);
        Assert.Equal(PlanningException.ConflictCode, ex.Code);

        await using var verify = factory.Create(tenantProvider);
        Assert.Equal(1, await verify.AssetAllocations.CountAsync(a => a.AssetId == seed.AssetId));
        Assert.Equal(1, await verify.Reservations.CountAsync(r => r.Purpose == ReservationPurpose.InternalUse));
        Assert.Equal(1, await verify.ReservationItems.CountAsync(i => i.RentalAssetId == seed.RentalAssetId));
    }

    private static async Task MakePlannableAsync(PostgresAppDbFactory factory, FakeTenantProvider tenantProvider, Seed seed)
    {
        await using var db = factory.Create(tenantProvider);
        db.AssetPlanningProfiles.Add(new AssetPlanningProfile
        {
            TenantId = seed.TenantId,
            AssetId = seed.AssetId,
            IsPlannable = true,
            Capacity = 1,
            HoursPolicy = AssetHoursPolicy.Anytime,
        });
        await db.SaveChangesAsync();
    }

    private static AssetAllocationService CreateAllocations(AppDbContext db, FakeTenantProvider tenantProvider)
    {
        var clock = BookingClock();
        var occupancy = new AssetOccupancyService(
            db,
            new FixedModuleAccessor(PlatformModules.ResourcePlanning, PlatformModules.Rentals),
            new ResourceOccupancyService(clock),
            clock);
        return new AssetAllocationService(
            db, tenantProvider, occupancy, new FakePlanningCaller(Guid.NewGuid(), canManage: false), clock);
    }
}
