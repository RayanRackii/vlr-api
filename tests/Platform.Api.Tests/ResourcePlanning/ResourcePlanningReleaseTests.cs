using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Modules.Rentals.Contracts;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.ResourcePlanning;

/// <summary>
/// Release/cancel occupancy semantics for Phase 0 (fix/rp-release-occupancy): cancelling before
/// the work starts frees the entire buffered window, an early release shortens the occupied end to
/// release-time + cleanup, a normal completion leaves the window untouched, and the freed time is
/// bookable again by both re-allocation and customer bookings. Also covers UTC normalization of a
/// -03:00 workStart and the resource-name conflict message.
/// </summary>
public sealed partial class ResourcePlanningConcurrencyTests
{
    [DockerFact]
    public async Task Cancel_before_start_frees_the_whole_window_including_buffers()
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant);

        await using var db = factory.Create(tenant);
        var requirement = await db.CatalogOrderItemResourceRequirements.SingleAsync();
        requirement.PrepMinutes = 15;
        requirement.CleanupMinutes = 15;
        // Allow the 90-minute buffered window (15 prep + 60 work + 15 cleanup) on this resource.
        var rental = await db.RentalAssets.SingleAsync(r => r.Id == seed.RentalAssetId);
        rental.AllowedDurationMinutes = "90";
        await db.SaveChangesAsync();

        var orders = CreateOrders(db, tenant);
        await orders.ApproveAsync(seed.OrderId,
            new() { UnitId = seed.UnitId, WorkStart = WorkStart, ExpectedVersion = 0 }, CancellationToken.None);
        var cancelled = await orders.CancelAsync(seed.OrderId, "customer changed their mind", CancellationToken.None);
        Assert.Equal(CatalogOrderStatus.Cancelled, cancelled.Status);

        await using var verify = factory.Create(tenant);
        var reservation = await verify.Reservations.SingleAsync(r => r.Purpose == ReservationPurpose.InternalUse);
        Assert.Equal(ReservationStatus.Canceled, reservation.Status);
        var allocation = await verify.CatalogOrderItemResourceAllocations.SingleAsync();
        Assert.Equal(AllocationStatus.Cancelled, allocation.Status);

        // The entire buffered window (prep + work + cleanup) is bookable again.
        var occupancy = new ResourceOccupancyService(BookingClock());
        var freed = await occupancy.CheckAvailabilityAsync(verify, seed.TenantId, seed.RentalAssetId,
            WorkStart.AddMinutes(-15), WorkEnd.AddMinutes(15), tentativeBatch: [], CancellationToken.None);
        Assert.True(freed.IsAvailable);
    }

    [DockerFact]
    public async Task Early_release_shortens_occupied_end_to_release_plus_cleanup()
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant);

        // Work window [WorkStart, WorkEnd) plus a 10-minute cleanup buffer.
        var cleanup = TimeSpan.FromMinutes(10);
        var releaseClock = new TestTimeProvider(WorkStart.AddMinutes(30));
        var occupancy = new ResourceOccupancyService(releaseClock);

        await using var db = factory.Create(tenant);
        var reservationItemId = await occupancy.CreateInternalUseOccupancyAsync(
            db, seed.TenantId, seed.UnitId, seed.RentalAssetId, 1, WorkStart, WorkEnd + cleanup, CancellationToken.None);
        await db.SaveChangesAsync();

        // Released 30 min in; occupied end collapses to 10:30 + 10 min cleanup = 10:40.
        await occupancy.ReleaseInternalUseOccupancyAsync(db, seed.TenantId, reservationItemId,
            InternalUseReleaseKind.ReleaseAfterStart, WorkEnd, CancellationToken.None);
        await db.SaveChangesAsync();

        await using var checkDb = factory.Create(tenant);
        var shortenedEnd = WorkStart.AddMinutes(40);
        // Still blocked up to the shortened end (release moment + cleanup).
        Assert.False((await occupancy.CheckAvailabilityAsync(checkDb, seed.TenantId, seed.RentalAssetId,
            WorkStart.AddMinutes(20), shortenedEnd, tentativeBatch: [], CancellationToken.None)).IsAvailable);
        // Everything from the shortened end onward — including the originally planned tail — is free.
        Assert.True((await occupancy.CheckAvailabilityAsync(checkDb, seed.TenantId, seed.RentalAssetId,
            shortenedEnd, WorkEnd + cleanup, tentativeBatch: [], CancellationToken.None)).IsAvailable);
    }

    [DockerFact]
    public async Task Release_at_planned_end_leaves_the_window_unchanged()
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant);

        var clock = new TestTimeProvider(WorkEnd);
        var occupancy = new ResourceOccupancyService(clock);

        await using var db = factory.Create(tenant);
        var reservationItemId = await occupancy.CreateInternalUseOccupancyAsync(
            db, seed.TenantId, seed.UnitId, seed.RentalAssetId, 1, WorkStart, WorkEnd, CancellationToken.None);
        await db.SaveChangesAsync();

        await occupancy.ReleaseInternalUseOccupancyAsync(db, seed.TenantId, reservationItemId,
            InternalUseReleaseKind.ReleaseAfterStart, WorkEnd, CancellationToken.None);
        await db.SaveChangesAsync();

        await using var verify = factory.Create(tenant);
        var reservation = await verify.Reservations.SingleAsync(r => r.Purpose == ReservationPurpose.InternalUse);
        Assert.Equal(ReservationStatus.Completed, reservation.Status);
        Assert.Equal(WorkEnd, reservation.EndDateTime);
        // The planned window still blocks (completion is not retroactive); later times are free.
        Assert.False((await occupancy.CheckAvailabilityAsync(verify, seed.TenantId, seed.RentalAssetId,
            WorkStart, WorkEnd, tentativeBatch: [], CancellationToken.None)).IsAvailable);
        Assert.True((await occupancy.CheckAvailabilityAsync(verify, seed.TenantId, seed.RentalAssetId,
            WorkEnd, WorkEnd.AddHours(1), tentativeBatch: [], CancellationToken.None)).IsAvailable);
    }

    [DockerFact]
    public async Task Reallocation_into_freed_slot_succeeds_after_cancel_before_start()
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant);

        await using var db = factory.Create(tenant);
        var secondOrderId = await AddSecondOrderAsync(db, seed, orderNumber: 2);
        var orders = CreateOrders(db, tenant);

        await orders.ApproveAsync(seed.OrderId,
            new() { UnitId = seed.UnitId, WorkStart = WorkStart, ExpectedVersion = 0 }, CancellationToken.None);
        await orders.CancelAsync(seed.OrderId, "freeing the slot", CancellationToken.None);

        // The second order can claim the exact same slot the cancelled one gave up.
        var second = await orders.ApproveAsync(secondOrderId,
            new() { UnitId = seed.UnitId, WorkStart = WorkStart, ExpectedVersion = 0 }, CancellationToken.None);
        Assert.Equal(CatalogOrderStatus.Approved, second.Status);
    }

    [DockerFact]
    public async Task Reallocation_into_freed_tail_succeeds_after_early_release()
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant);

        await using var setup = factory.Create(tenant);
        var secondOrderId = await AddSecondOrderAsync(setup, seed, orderNumber: 2);

        await using (var approveDb = factory.Create(tenant))
        {
            var orders = CreateOrders(approveDb, tenant);
            await orders.ApproveAsync(seed.OrderId,
                new() { UnitId = seed.UnitId, WorkStart = WorkStart, ExpectedVersion = 0 }, CancellationToken.None);
            await orders.StartPreparingAsync(seed.OrderId, CancellationToken.None);
        }

        // Finish work, then release 30 min into the planned hour: occupied end collapses to 10:30.
        await using (var releaseDb = factory.Create(tenant))
        {
            var releaseClock = new TestTimeProvider(WorkStart.AddMinutes(30));
            var usage = new CatalogOrderAllocationUsageService(releaseDb, tenant, new ResourceOccupancyService(releaseClock));
            var allocation = await releaseDb.CatalogOrderItemResourceAllocations.SingleAsync(a => a.RentalAssetId == seed.RentalAssetId);
            await usage.ApplyUsageActionAsync(seed.OrderId, allocation.Id,
                new() { Action = "work-finished", ExpectedVersion = allocation.PlanningRevision }, CancellationToken.None);
            var cleaning = await releaseDb.CatalogOrderItemResourceAllocations.SingleAsync(a => a.RentalAssetId == seed.RentalAssetId);
            await usage.ApplyUsageActionAsync(seed.OrderId, cleaning.Id,
                new() { Action = "release", ExpectedVersion = cleaning.PlanningRevision }, CancellationToken.None);
        }

        // 10:45 fell inside the original planned hour but is now in the freed tail.
        await using var approve2 = factory.Create(tenant);
        var second = await CreateOrders(approve2, tenant).ApproveAsync(secondOrderId,
            new() { UnitId = seed.UnitId, WorkStart = WorkStart.AddMinutes(45), ExpectedVersion = 0 }, CancellationToken.None);
        Assert.Equal(CatalogOrderStatus.Approved, second.Status);
    }

    [DockerFact]
    public async Task Customer_booking_into_freed_slot_succeeds_after_cancel()
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant);
        var clock = BookingClock();

        await using var db = factory.Create(tenant);
        var orders = CreateOrders(db, tenant);
        await orders.ApproveAsync(seed.OrderId,
            new() { UnitId = seed.UnitId, WorkStart = WorkStart, ExpectedVersion = 0 }, CancellationToken.None);
        await orders.CancelAsync(seed.OrderId, "customer booking takes over", CancellationToken.None);

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

        var reservations = new ReservationService(db, tenant, new FakeTrialGuard(),
            TestReservationQueue.Create(db, tenant),
            SilentRentalsNotifications.Publisher.AsDomainEvents(), SilentRentalsNotifications.Scheduler, clock);
        var booking = await reservations.CreateReservationAsync(seed.CustomerId, new CreateReservationRequestDto
        {
            UnitId = seed.UnitId, Date = Date, StartTime = WorkStartLocal, EndTime = WorkEndLocal,
            Items = [new CreateReservationItemRequestDto { AssetId = seed.AssetId, Quantity = 1 }],
        }, CancellationToken.None);
        Assert.NotNull(booking);

        await using var verify = factory.Create(tenant);
        Assert.True(await verify.Reservations.AnyAsync(r => r.Purpose == ReservationPurpose.CustomerBooking));
    }

    [DockerFact]
    public async Task Releasing_one_resource_does_not_free_another()
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant);
        var occupancy = new ResourceOccupancyService(BookingClock());

        await using var db = factory.Create(tenant);
        var secondResourceId = await AddSecondResourceAsync(db, seed);
        var itemA = await occupancy.CreateInternalUseOccupancyAsync(
            db, seed.TenantId, seed.UnitId, seed.RentalAssetId, 1, WorkStart, WorkEnd, CancellationToken.None);
        await occupancy.CreateInternalUseOccupancyAsync(
            db, seed.TenantId, seed.UnitId, secondResourceId, 1, WorkStart, WorkEnd, CancellationToken.None);
        await db.SaveChangesAsync();

        await occupancy.ReleaseInternalUseOccupancyAsync(db, seed.TenantId, itemA,
            InternalUseReleaseKind.CancelBeforeStart, WorkEnd, CancellationToken.None);
        await db.SaveChangesAsync();

        await using var verify = factory.Create(tenant);
        Assert.True((await occupancy.CheckAvailabilityAsync(verify, seed.TenantId, seed.RentalAssetId,
            WorkStart, WorkEnd, tentativeBatch: [], CancellationToken.None)).IsAvailable);
        Assert.False((await occupancy.CheckAvailabilityAsync(verify, seed.TenantId, secondResourceId,
            WorkStart, WorkEnd, tentativeBatch: [], CancellationToken.None)).IsAvailable);
    }

    [DockerFact]
    public async Task Approve_and_preview_accept_a_minus_three_offset_work_start()
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant);

        // Same instant as WorkStart (10:00 America/Sao_Paulo) but carrying an explicit -03:00 offset,
        // exactly as a browser client would serialize it.
        var offsetWorkStart = new DateTimeOffset(Date.Year, Date.Month, Date.Day, 10, 0, 0, TimeSpan.FromHours(-3));

        await using var previewDb = factory.Create(tenant);
        var item = await previewDb.CatalogOrderItems.SingleAsync();
        var preview = await new CatalogOrderAllocationPreviewService(previewDb, tenant, new ResourceOccupancyService(BookingClock()))
            .PreviewAsync(seed.OrderId, new CatalogOrderAllocationPreviewRequest
            {
                UnitId = seed.UnitId,
                WorkStarts = [new AllocationPreviewWorkStart { OrderItemId = item.Id, WorkStart = offsetWorkStart }],
            }, CancellationToken.None);
        Assert.True(preview.IsFeasible);

        await using var approveDb = factory.Create(tenant);
        var approved = await CreateOrders(approveDb, tenant).ApproveAsync(seed.OrderId,
            new() { UnitId = seed.UnitId, WorkStart = offsetWorkStart, ExpectedVersion = 0 }, CancellationToken.None);
        Assert.Equal(CatalogOrderStatus.Approved, approved.Status);

        await using var verify = factory.Create(tenant);
        var reservation = await verify.Reservations.SingleAsync(r => r.Purpose == ReservationPurpose.InternalUse);
        Assert.Equal(WorkStart, reservation.StartDateTime);
    }

    [DockerFact]
    public async Task Conflict_message_uses_resource_name_not_id()
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant);

        await using var db = factory.Create(tenant);
        var secondOrderId = await AddSecondOrderAsync(db, seed, orderNumber: 2);
        var orders = CreateOrders(db, tenant);
        await orders.ApproveAsync(seed.OrderId,
            new() { UnitId = seed.UnitId, WorkStart = WorkStart, ExpectedVersion = 0 }, CancellationToken.None);

        var conflict = await Assert.ThrowsAsync<ResourceUnavailableException>(() => orders.ApproveAsync(secondOrderId,
            new() { UnitId = seed.UnitId, WorkStart = WorkStart, ExpectedVersion = 0 }, CancellationToken.None));
        Assert.Contains("Impressora 3D", conflict.Message);
        Assert.DoesNotContain(seed.RentalAssetId.ToString(), conflict.Message);
    }

    [DockerFact]
    public async Task Release_before_occupied_start_cancels_reservation_and_frees_slot()
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant);

        // Release (no cleanup buffer) 5 min before the window even opens: nothing is left to
        // occupy, so the whole window must be freed as Canceled, not left blocking as Completed.
        var releaseClock = new TestTimeProvider(WorkStart.AddMinutes(-5));
        var occupancy = new ResourceOccupancyService(releaseClock);

        await using var db = factory.Create(tenant);
        var reservationItemId = await occupancy.CreateInternalUseOccupancyAsync(
            db, seed.TenantId, seed.UnitId, seed.RentalAssetId, 1, WorkStart, WorkEnd, CancellationToken.None);
        await db.SaveChangesAsync();

        await occupancy.ReleaseInternalUseOccupancyAsync(db, seed.TenantId, reservationItemId,
            InternalUseReleaseKind.ReleaseAfterStart, WorkEnd, CancellationToken.None);
        await db.SaveChangesAsync();

        await using var verify = factory.Create(tenant);
        var reservation = await verify.Reservations.SingleAsync(r => r.Purpose == ReservationPurpose.InternalUse);
        Assert.Equal(ReservationStatus.Canceled, reservation.Status);
        Assert.True((await occupancy.CheckAvailabilityAsync(verify, seed.TenantId, seed.RentalAssetId,
            WorkStart, WorkEnd, tentativeBatch: [], CancellationToken.None)).IsAvailable);
    }

    [DockerFact]
    public async Task Release_exactly_at_work_start_frees_the_window()
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant);

        // Boundary: released at the work start with no cleanup, so shortenedEnd == occupiedStart.
        // `<= occupiedStart` must treat this as "nothing left to occupy" and free the window.
        var releaseClock = new TestTimeProvider(WorkStart);
        var occupancy = new ResourceOccupancyService(releaseClock);

        await using var db = factory.Create(tenant);
        var reservationItemId = await occupancy.CreateInternalUseOccupancyAsync(
            db, seed.TenantId, seed.UnitId, seed.RentalAssetId, 1, WorkStart, WorkEnd, CancellationToken.None);
        await db.SaveChangesAsync();

        await occupancy.ReleaseInternalUseOccupancyAsync(db, seed.TenantId, reservationItemId,
            InternalUseReleaseKind.ReleaseAfterStart, WorkEnd, CancellationToken.None);
        await db.SaveChangesAsync();

        await using var verify = factory.Create(tenant);
        var reservation = await verify.Reservations.SingleAsync(r => r.Purpose == ReservationPurpose.InternalUse);
        Assert.Equal(ReservationStatus.Canceled, reservation.Status);
        Assert.True((await occupancy.CheckAvailabilityAsync(verify, seed.TenantId, seed.RentalAssetId,
            WorkStart, WorkEnd, tentativeBatch: [], CancellationToken.None)).IsAvailable);
    }

    [DockerFact]
    public async Task Two_approvals_competing_for_a_freed_slot_allow_exactly_one_winner()
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant);

        // Occupy the slot, then release-before-start to free it the Canceled way.
        await using (var freeDb = factory.Create(tenant))
        {
            var occupancy = new ResourceOccupancyService(new TestTimeProvider(WorkStart.AddMinutes(-5)));
            var itemId = await occupancy.CreateInternalUseOccupancyAsync(
                freeDb, seed.TenantId, seed.UnitId, seed.RentalAssetId, 1, WorkStart, WorkEnd, CancellationToken.None);
            await freeDb.SaveChangesAsync();
            await occupancy.ReleaseInternalUseOccupancyAsync(freeDb, seed.TenantId, itemId,
                InternalUseReleaseKind.ReleaseAfterStart, WorkEnd, CancellationToken.None);
            await freeDb.SaveChangesAsync();
        }

        await using var setup = factory.Create(tenant);
        var secondOrderId = await AddSecondOrderAsync(setup, seed, orderNumber: 2);

        await using var db1 = factory.Create(tenant);
        await using var db2 = factory.Create(tenant);
        var orders1 = CreateOrders(db1, tenant);
        var orders2 = CreateOrders(db2, tenant);

        var captured = await Task.WhenAll(
            CaptureAsync(() => orders1.ApproveAsync(seed.OrderId,
                new() { UnitId = seed.UnitId, WorkStart = WorkStart, ExpectedVersion = 0 }, CancellationToken.None)),
            CaptureAsync(() => orders2.ApproveAsync(secondOrderId,
                new() { UnitId = seed.UnitId, WorkStart = WorkStart, ExpectedVersion = 0 }, CancellationToken.None)));

        // Exactly one approval into the freed slot wins; the other loses the real-Postgres race.
        Assert.Single(captured, c => c.Error is null);
        Assert.Single(captured, c => c.Error is not null);

        await using var verify = factory.Create(tenant);
        Assert.Equal(1, await verify.CatalogOrderItemResourceAllocations.CountAsync(a => a.TenantId == seed.TenantId));
        Assert.Equal(1, await verify.Reservations.CountAsync(r =>
            r.TenantId == seed.TenantId && r.Purpose == ReservationPurpose.InternalUse
            && r.Status != ReservationStatus.Canceled));
    }

    private static async Task<Guid> AddSecondOrderAsync(AppDbContext db, Seed seed, int orderNumber)
    {
        var order = new CatalogOrder
        {
            TenantId = seed.TenantId, CustomerId = seed.CustomerId, OrderNumber = orderNumber,
            CustomerNameSnapshot = "Ana", Currency = "BRL",
        };
        var item = new CatalogOrderItem
        {
            TenantId = seed.TenantId, OrderId = order.Id, ProductId = seed.ProductId,
            ProductNameSnapshot = "Impressao 3D", Quantity = 1, UnitPriceSnapshot = 50m,
            SubTotal = 50m, Currency = "BRL",
        };
        order.AddItem(item);
        db.CatalogOrders.Add(order);
        db.CatalogOrderItemResourceRequirements.Add(new CatalogOrderItemResourceRequirement
        {
            TenantId = seed.TenantId, OrderItemId = item.Id, RentalAssetId = seed.RentalAssetId,
            ResourceQuantity = 1, DurationMinutesPerUnit = 60,
        });
        await db.SaveChangesAsync();
        return order.Id;
    }

    private static async Task<Guid> AddSecondResourceAsync(AppDbContext db, Seed seed)
    {
        var baseAsset = await db.Assets.AsNoTracking().FirstAsync(a => a.Id == seed.AssetId);
        var asset = new Asset
        {
            TenantId = seed.TenantId, UnitId = baseAsset.UnitId, CategoryId = baseAsset.CategoryId,
            FamilyId = baseAsset.FamilyId, Name = "Impressora 3D #2", Tag = "P2",
            Status = AssetStatus.Active, IsRentable = true,
        };
        var rental = new RentalAsset
        {
            TenantId = seed.TenantId, AssetId = asset.Id, Type = RentalAssetType.Location,
            TotalQuantity = 1, IsActive = true, RequiresDeposit = false,
            SchedulePolicy = SchedulePolicy.OpenHours, OpenTime = new TimeOnly(8, 0),
            CloseTime = new TimeOnly(22, 0), QueueEnabled = false,
        };
        db.Assets.Add(asset);
        db.RentalAssets.Add(rental);
        await db.SaveChangesAsync();
        return rental.Id;
    }
}
