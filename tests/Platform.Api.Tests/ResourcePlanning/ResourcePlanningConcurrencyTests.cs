using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Modules.Admin.Controllers;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Modules.Admin.Services;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Authentication;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Modules.Rentals.Contracts;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Notifications;
using Platform.Api.Services.Tenancy;
using Platform.Api.Storage;
using Platform.Api.Tests.Catalog;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Domain.Exceptions;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.ResourcePlanning;

/// <summary>
/// Real-PostgreSQL coverage for Phase 0 of Order-Resource Planning
/// (order-equipment-link-codex.md), per BRIEF-API.md's "Tests (required)" list. In-memory
/// providers never exercise the Npgsql advisory locks (<see cref="RentalAssetLocks"/>,
/// <see cref="AdminTenantService"/>'s module lock) or real row-level UPDATE conflicts these
/// scenarios depend on.
/// </summary>
public sealed partial class ResourcePlanningConcurrencyTests : IClassFixture<PostgresContainerFixture>
{
    private static readonly DateOnly Date = new(2026, 9, 1);

    private readonly PostgresContainerFixture _postgres;

    public ResourcePlanningConcurrencyTests(PostgresContainerFixture postgres)
    {
        _postgres = postgres;
    }

    [DockerFact]
    public async Task Booking_vs_approve_with_planning_on_same_resource_allows_only_one_winner()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenantProvider);

        await using var dbBooking = factory.Create(tenantProvider);
        await using var dbApprove = factory.Create(tenantProvider);

        var reservationService = new ReservationService(
            dbBooking,
            tenantProvider,
            new FakeTrialGuard(),
            TestReservationQueue.Create(dbBooking, tenantProvider),
            SilentRentalsNotifications.Publisher.AsDomainEvents(),
            SilentRentalsNotifications.Scheduler,
            BookingClock());
        var orders = CreateOrders(dbApprove, tenantProvider);

        var bookingRequest = new Platform.Api.Modules.Rentals.Dtos.CreateReservationRequestDto
        {
            UnitId = seed.UnitId,
            Date = Date,
            StartTime = WorkStartLocal,
            EndTime = WorkEndLocal,
            Items = [new Platform.Api.Modules.Rentals.Dtos.CreateReservationItemRequestDto { AssetId = seed.AssetId, Quantity = 1 }],
        };
        var planning = new CatalogOrderApprovePlanningRequest
        {
            UnitId = seed.UnitId,
            WorkStart = WorkStart,
            ExpectedVersion = 0,
        };

        var bookingTask = CaptureAsync(() => reservationService.CreateReservationAsync(seed.CustomerId, bookingRequest, CancellationToken.None));
        var approveTask = CaptureAsync(() => orders.ApproveAsync(seed.OrderId, planning, CancellationToken.None));
        await Task.WhenAll(bookingTask, approveTask);
        var bookingResult = await bookingTask;
        var approveResult = await approveTask;

        var errors = new[] { bookingResult.Error, approveResult.Error };
        Assert.Single(errors, e => e is null);
        Assert.Single(errors, e => e is not null);

        await using var verify = factory.Create(tenantProvider);
        var itemCount = await verify.ReservationItems
            .CountAsync(i => i.RentalAssetId == seed.RentalAssetId);
        Assert.Equal(1, itemCount);
    }

    [DockerFact]
    public async Task Concurrent_approve_with_planning_creates_exactly_one_allocation()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenantProvider);

        await using var db1 = factory.Create(tenantProvider);
        await using var db2 = factory.Create(tenantProvider);
        var orders1 = CreateOrders(db1, tenantProvider);
        var orders2 = CreateOrders(db2, tenantProvider);

        var planning = new CatalogOrderApprovePlanningRequest
        {
            UnitId = seed.UnitId,
            WorkStart = WorkStart,
            ExpectedVersion = 0,
        };

        var captured = await Task.WhenAll(
            CaptureAsync(() => orders1.ApproveAsync(seed.OrderId, planning, CancellationToken.None)),
            CaptureAsync(() => orders2.ApproveAsync(seed.OrderId, planning, CancellationToken.None)));

        Assert.Contains(captured, r => r.Error is null);

        await using var verify = factory.Create(tenantProvider);
        Assert.Equal(1, await verify.CatalogOrderItemResourceAllocations.CountAsync(a => a.TenantId == seed.TenantId));
        Assert.Equal(
            1,
            await verify.Reservations.CountAsync(r => r.TenantId == seed.TenantId && r.Purpose == ReservationPurpose.InternalUse));
        var order = await verify.CatalogOrders.SingleAsync(o => o.Id == seed.OrderId);
        Assert.Equal(CatalogOrderStatus.Approved, order.Status);
    }

    [DockerFact]
    public async Task Approve_rolls_back_entirely_when_resource_already_occupied()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenantProvider);

        // Pre-occupy the exact buffered interval with a confirmed customer booking.
        await using (var seedDb = factory.Create(tenantProvider))
        {
            var blocker = new Reservation
            {
                Purpose = ReservationPurpose.CustomerBooking,
                TenantId = seed.TenantId,
                UnitId = seed.UnitId,
                CustomerId = seed.CustomerId,
                CustomerName = "Existing",
                CustomerWhatsApp = "11999999999",
                StartDateTime = WorkStart,
                EndDateTime = WorkEnd,
                Status = ReservationStatus.Confirmed,
                TotalAmount = 100m,
                DepositPaid = 0m,
            };
            blocker.AddItem(new ReservationItem
            {
                TenantId = seed.TenantId,
                ReservationId = blocker.Id,
                RentalAssetId = seed.RentalAssetId,
                Quantity = 1,
                UnitPrice = 100m,
                SubTotal = 100m,
            });
            seedDb.Reservations.Add(blocker);
            await seedDb.SaveChangesAsync();
        }

        await using var db = factory.Create(tenantProvider);
        var orders = CreateOrders(db, tenantProvider);
        var planning = new CatalogOrderApprovePlanningRequest
        {
            UnitId = seed.UnitId,
            WorkStart = WorkStart,
            ExpectedVersion = 0,
        };

        await Assert.ThrowsAsync<ResourceUnavailableException>(
            () => orders.ApproveAsync(seed.OrderId, planning, CancellationToken.None));

        await using var verify = factory.Create(tenantProvider);
        var order = await verify.CatalogOrders.SingleAsync(o => o.Id == seed.OrderId);
        Assert.Equal(CatalogOrderStatus.Requested, order.Status);
        Assert.Equal(0, await verify.CatalogOrderItemResourceAllocations.CountAsync(a => a.TenantId == seed.TenantId));
        Assert.Equal(
            0,
            await verify.Reservations.CountAsync(r => r.TenantId == seed.TenantId && r.Purpose == ReservationPurpose.InternalUse));
    }

    [DockerFact]
    public async Task Module_disable_and_approve_serialize_never_leaving_an_active_allocation_under_a_disabled_module()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenantProvider);

        await using var dbApprove = factory.Create(tenantProvider);
        var orders = CreateOrders(dbApprove, tenantProvider);
        var planning = new CatalogOrderApprovePlanningRequest
        {
            UnitId = seed.UnitId,
            WorkStart = WorkStart,
            ExpectedVersion = 0,
        };

        var disableRequest = await BuildDisableResourcePlanningRequestAsync(factory, seed.TenantId);

        var approveTask = CaptureAsync(() => orders.ApproveAsync(seed.OrderId, planning, CancellationToken.None));
        var disableTask = CaptureUpdateAsync(factory, seed.TenantId, disableRequest);
        await Task.WhenAll(approveTask, disableTask);
        var approveOutcome = await approveTask;

        await using var verify = factory.Create(tenantProvider);
        var moduleActive = await verify.TenantModules
            .IgnoreQueryFilters()
            .AnyAsync(m => m.TenantId == seed.TenantId && m.ModuleName == PlatformModules.ResourcePlanning && m.IsActive);
        var unreleasedAllocationExists = await verify.CatalogOrderItemResourceAllocations
            .AnyAsync(a => a.TenantId == seed.TenantId && a.Status == AllocationStatus.Scheduled);

        // Invariant: never both "module disabled" and "an active allocation exists" at once —
        // either the disable lost the race (module stays active, allocation exists) or the
        // approval lost the race (module disabled, no allocation was created).
        Assert.False(!moduleActive && unreleasedAllocationExists);
        if (!moduleActive)
        {
            Assert.False(unreleasedAllocationExists);
        }

        if (approveOutcome.Error is null)
        {
            Assert.True(moduleActive);
            Assert.True(unreleasedAllocationExists);
        }
    }

    [DockerFact]
    public async Task Cross_tenant_rental_asset_in_requirement_snapshot_is_rejected_by_the_database()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seedA = await SeedAsync(factory, tenantProvider);
        var otherProvider = new FakeTenantProvider();
        var seedB = await SeedAsync(factory, otherProvider);

        // Bypass the application-level tenant check and attempt to persist a snapshot whose
        // RentalAssetId belongs to a different tenant than the row's own TenantId. The composite
        // tenant-qualified FK (TenantId, RentalAssetId) -> RentalAsset(TenantId, Id) must reject
        // this at the database level even though both ids independently exist.
        await using var db = factory.Create(tenantProvider);
        var orderItem = await db.CatalogOrderItems.Include(i => i.Order).FirstAsync(i => i.Order.TenantId == seedA.TenantId);
        db.CatalogOrderItemResourceRequirements.Add(new CatalogOrderItemResourceRequirement
        {
            TenantId = seedA.TenantId,
            OrderItemId = orderItem.Id,
            RentalAssetId = seedB.RentalAssetId, // belongs to tenant B, row claims tenant A
            ResourceQuantity = 1,
            DurationMinutesPerUnit = 60,
        });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [DockerFact]
    public async Task Cross_tenant_order_id_in_requirements_put_returns_not_found()
    {
        // Product resource-requirements PUT scopes by (TenantId, ProductId); a cross-tenant
        // product id must 404, never leak existence via 403 — verified at the data-access layer
        // the endpoint relies on (ambient tenant query filter).
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seedA = await SeedAsync(factory, tenantProvider);
        var otherProvider = new FakeTenantProvider();
        var seedB = await SeedAsync(factory, otherProvider);

        await using var dbScopedToA = factory.Create(tenantProvider);
        var crossTenantLookup = await dbScopedToA.CatalogProducts
            .FirstOrDefaultAsync(p => p.Id == seedB.ProductId);
        Assert.Null(crossTenantLookup);
    }

    [DockerFact]
    public async Task InternalUse_buffers_make_the_full_interval_unavailable_not_just_the_work_window()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenantProvider);

        var occupiedStart = WorkStart - TimeSpan.FromMinutes(15);
        var occupiedEnd = WorkEnd + TimeSpan.FromMinutes(15);

        await using var seedDb = factory.Create(tenantProvider);
        var occupancy = new ResourceOccupancyService();
        var reservationItemId = await occupancy.CreateInternalUseOccupancyAsync(
            seedDb, seed.TenantId, seed.UnitId, seed.RentalAssetId, 1, occupiedStart, occupiedEnd, CancellationToken.None);
        await seedDb.SaveChangesAsync();

        // Entirely inside the prep buffer (before the raw work window) must be rejected.
        var bufferConflict = await occupancy.CheckAvailabilityAsync(
            seedDb, seed.TenantId, seed.RentalAssetId,
            occupiedStart, occupiedStart.AddMinutes(10), tentativeBatch: [], CancellationToken.None);
        Assert.False(bufferConflict.IsAvailable);

        // Explicitly release (H4: an unreleased InternalUse occupancy now blocks indefinitely past
        // its planned end, so this buffer-boundary assertion needs the occupancy released first to
        // isolate "outside the buffer" from "still unreleased").
        await occupancy.ReleaseInternalUseOccupancyAsync(seedDb, seed.TenantId, reservationItemId, CancellationToken.None);
        await seedDb.SaveChangesAsync();

        // Entirely outside the buffered interval must be available.
        var clear = await occupancy.CheckAvailabilityAsync(
            seedDb, seed.TenantId, seed.RentalAssetId,
            occupiedEnd.AddMinutes(1), occupiedEnd.AddMinutes(30), tentativeBatch: [], CancellationToken.None);
        Assert.True(clear.IsAvailable);
    }

    [DockerFact]
    public async Task Overdue_unreleased_internal_use_still_blocks_a_new_overlapping_request()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenantProvider);

        var pastStart = DateTimeOffset.UtcNow.AddDays(-5);
        var pastEnd = pastStart.AddHours(1);

        await using var seedDb = factory.Create(tenantProvider);
        var occupancy = new ResourceOccupancyService();
        var reservationItemId = await occupancy.CreateInternalUseOccupancyAsync(
            seedDb, seed.TenantId, seed.UnitId, seed.RentalAssetId, 1, pastStart, pastEnd, CancellationToken.None);
        await seedDb.SaveChangesAsync();

        // Status is still Confirmed (never released) even though the planned end is long past —
        // "now" must never be treated as an implicit release.
        var result = await occupancy.CheckAvailabilityAsync(
            seedDb, seed.TenantId, seed.RentalAssetId, pastStart, pastEnd, tentativeBatch: [], CancellationToken.None);
        Assert.False(result.IsAvailable);

        // H2 (query security review finding): a brand-new request that starts strictly *after* the
        // overdue reservation's planned end must still be rejected while it remains unreleased —
        // the old EndDateTime-bounded overlap check would have wrongly freed the resource here.
        var afterPlannedEnd = await occupancy.CheckAvailabilityAsync(
            seedDb, seed.TenantId, seed.RentalAssetId,
            pastEnd.AddDays(1), pastEnd.AddDays(1).AddHours(1), tentativeBatch: [], CancellationToken.None);
        Assert.False(afterPlannedEnd.IsAvailable);

        // Explicit release now frees it — a later request is available again.
        await occupancy.ReleaseInternalUseOccupancyAsync(seedDb, seed.TenantId, reservationItemId, CancellationToken.None);
        await seedDb.SaveChangesAsync();

        var afterRelease = await occupancy.CheckAvailabilityAsync(
            seedDb, seed.TenantId, seed.RentalAssetId,
            pastEnd.AddDays(1), pastEnd.AddDays(1).AddHours(1), tentativeBatch: [], CancellationToken.None);
        Assert.True(afterRelease.IsAvailable);
    }

    [DockerFact]
    public async Task Cancel_while_running_keeps_the_allocation_occupied_until_explicit_release()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenantProvider);

        await using var db = factory.Create(tenantProvider);
        var orders = CreateOrders(db, tenantProvider);
        var planning = new CatalogOrderApprovePlanningRequest
        {
            UnitId = seed.UnitId,
            WorkStart = WorkStart,
            ExpectedVersion = 0,
        };
        await orders.ApproveAsync(seed.OrderId, planning, CancellationToken.None);
        await orders.StartPreparingAsync(seed.OrderId, CancellationToken.None);

        var allocation = await db.CatalogOrderItemResourceAllocations.SingleAsync(a => a.TenantId == seed.TenantId);
        Assert.Equal(AllocationStatus.Running, allocation.Status);

        var cancelled = await orders.CancelAsync(seed.OrderId, "customer no-show", CancellationToken.None);
        Assert.Equal(CatalogOrderStatus.Cancelled, cancelled.Status);

        await using var verify = factory.Create(tenantProvider);
        var allocationAfterCancel = await verify.CatalogOrderItemResourceAllocations.SingleAsync(a => a.TenantId == seed.TenantId);
        Assert.Equal(AllocationStatus.Running, allocationAfterCancel.Status);

        var reservation = await verify.Reservations.SingleAsync(r => r.TenantId == seed.TenantId && r.Purpose == ReservationPurpose.InternalUse);
        Assert.Equal(ReservationStatus.Confirmed, reservation.Status);
        var stillBlocked = await new ResourceOccupancyService().CheckAvailabilityAsync(
            verify, seed.TenantId, seed.RentalAssetId, WorkStart, WorkEnd, tentativeBatch: [], CancellationToken.None);
        Assert.False(stillBlocked.IsAvailable);

        // Explicit release now frees it.
        await using var releaseDb = factory.Create(tenantProvider);
        var allocationBefore = await releaseDb.CatalogOrderItemResourceAllocations.SingleAsync(a => a.TenantId == seed.TenantId);
        // Work must finish before release is a valid transition from Running.
        var usage = new CatalogOrderAllocationUsageService(releaseDb, tenantProvider, new ResourceOccupancyService());
        await usage.ApplyUsageActionAsync(
            seed.OrderId,
            allocationBefore.Id,
            new CatalogOrderAllocationUsageRequest { Action = "work-finished", ExpectedVersion = allocationBefore.PlanningRevision },
            CancellationToken.None);
        var afterWorkFinished = await releaseDb.CatalogOrderItemResourceAllocations.SingleAsync(a => a.TenantId == seed.TenantId);
        await usage.ApplyUsageActionAsync(
            seed.OrderId,
            afterWorkFinished.Id,
            new CatalogOrderAllocationUsageRequest { Action = "release", ExpectedVersion = afterWorkFinished.PlanningRevision },
            CancellationToken.None);

        await using var finalVerify = factory.Create(tenantProvider);
        var finalAllocation = await finalVerify.CatalogOrderItemResourceAllocations.SingleAsync(a => a.TenantId == seed.TenantId);
        Assert.Equal(AllocationStatus.Released, finalAllocation.Status);
        Assert.NotNull(finalAllocation.ActualReleaseAt);

        // Released is no longer counted as "unreleased" by the module-disable guard — the exact
        // historical [WorkStart, WorkEnd) interval itself still blocks forever once the backing
        // Reservation reaches Completed, identical to existing CustomerBooking behavior ("Completion
        // currently does not release time; preserve that customer behavior" — spec §3). Release's
        // effect is administrative (frees the allocation from the unreleased set), not retroactive
        // availability of that exact past interval.
        var unreleasedCount = await finalVerify.CatalogOrderItemResourceAllocations
            .CountAsync(a => a.TenantId == seed.TenantId
                && (a.Status == AllocationStatus.Scheduled || a.Status == AllocationStatus.Running || a.Status == AllocationStatus.Cleaning));
        Assert.Equal(0, unreleasedCount);

        // A different, non-overlapping future interval on the same resource is free.
        var laterStart = WorkEnd.AddDays(1);
        var freed = await new ResourceOccupancyService().CheckAvailabilityAsync(
            finalVerify, seed.TenantId, seed.RentalAssetId, laterStart, laterStart.AddHours(1), tentativeBatch: [], CancellationToken.None);
        Assert.True(freed.IsAvailable);
    }

    [DockerFact]
    public async Task Migration_backfills_existing_reservations_to_customer_booking_by_column_default()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenantProvider);

        // Insert a row the way a pre-migration writer would have: omit the `purpose` column
        // entirely. The migration's AddColumn default ('CustomerBooking') is what backfills every
        // pre-existing row — this is the live-database equivalent of that backfill, since a
        // genuinely pre-migration schema no longer exists to insert against directly.
        await using var db = factory.Create(tenantProvider);
        var reservationId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO rentals.reservations
                (id, tenant_id, unit_id, customer_id, customer_name, customer_whats_app,
                 start_date_time, end_date_time, status, total_amount, deposit_paid, created_at)
            VALUES
                ({reservationId}, {seed.TenantId}, {seed.UnitId}, {seed.CustomerId}, 'Legacy', '11999999999',
                 {WorkStart}, {WorkEnd}, 'Confirmed', 0, 0, now())
            """);

        var purpose = await db.Reservations
            .Where(r => r.Id == reservationId)
            .Select(r => r.Purpose)
            .SingleAsync();
        Assert.Equal(ReservationPurpose.CustomerBooking, purpose);
    }

    private static TimeProvider BookingClock() =>
        new TestTimeProvider(
            Platform.Core.Infrastructure.Time.BrazilTimeZone.AtLocal(Date, new TimeOnly(9, 0)));

    private static readonly TimeOnly WorkStartLocal = new(10, 0);
    private static readonly TimeOnly WorkEndLocal = new(11, 0);

    private static DateTimeOffset WorkStart =>
        Platform.Core.Infrastructure.Time.BrazilTimeZone.AtLocal(Date, WorkStartLocal);

    private static DateTimeOffset WorkEnd =>
        Platform.Core.Infrastructure.Time.BrazilTimeZone.AtLocal(Date, WorkEndLocal);

    private PostgresAppDbFactory RequireFactory()
    {
        Assert.NotNull(_postgres.Factory);
        return _postgres.Factory!;
    }

    private static CatalogOrderService CreateOrders(AppDbContext db, FakeTenantProvider tenantProvider)
    {
        var publisher = new CatalogNotificationPublisher(
            db,
            tenantProvider,
            new FakePermissionResolver(),
            NullLogger<CatalogNotificationPublisher>.Instance);
        var occupancy = new ResourceOccupancyService();
        var scheduler = new RecordingOutboxScheduler();
        var allocationService = new CatalogOrderAllocationService(
            db, tenantProvider, occupancy, publisher.AsDomainEvents(), scheduler);
        return new CatalogOrderService(
            db,
            tenantProvider,
            new FakeHttpContextAccessor(),
            publisher.AsDomainEvents(),
            scheduler,
            allocationService,
            occupancy);
    }

    private static async Task<Seed> SeedAsync(PostgresAppDbFactory factory, FakeTenantProvider tenantProvider)
    {
        await using var db = factory.Create(tenantProvider);

        var tenant = new Tenant("Planning Club", UniqueTaxId(), subdomain: $"rp-{Guid.NewGuid():N}"[..16]);
        var unit = new Unit(tenant.Id, "Matriz");
        var category = new AssetCategory { TenantId = tenant.Id, Name = "Equipamentos" };
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
            Name = "Impressora 3D",
            Tag = "P1",
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
            OpenTime = new TimeOnly(8, 0),
            CloseTime = new TimeOnly(22, 0),
            QueueEnabled = false,
        };
        var customer = new Customer
        {
            TenantId = tenant.Id,
            Name = "Ana",
            Email = $"{Guid.NewGuid():N}@club.test",
            Phone = "11999999999",
        };
        var product = new CatalogProduct
        {
            TenantId = tenant.Id,
            Name = "Impressao 3D",
            Price = 50m,
            Currency = "BRL",
            IsActive = true,
        };

        db.Tenants.Add(tenant);
        db.Units.Add(unit);
        db.AssetCategories.Add(category);
        db.AssetFamilies.Add(family);
        db.Assets.Add(asset);
        db.RentalAssets.Add(rental);
        db.Customers.Add(customer);
        db.CatalogProducts.Add(product);
        db.TenantModules.Add(new TenantModule(tenant.Id, PlatformModules.Catalog, isActive: true));
        db.TenantModules.Add(new TenantModule(tenant.Id, PlatformModules.Rentals, isActive: true));
        db.TenantModules.Add(new TenantModule(tenant.Id, PlatformModules.ResourcePlanning, isActive: true));
        db.CatalogOrderNumberSequences.Add(new CatalogOrderNumberSequence { TenantId = tenant.Id, LastNumber = 1 });

        var order = new CatalogOrder
        {
            TenantId = tenant.Id,
            CustomerId = customer.Id,
            OrderNumber = 1,
            CustomerNameSnapshot = customer.Name,
            Currency = "BRL",
        };
        var item = new CatalogOrderItem
        {
            TenantId = tenant.Id,
            OrderId = order.Id,
            ProductId = product.Id,
            ProductNameSnapshot = product.Name,
            Quantity = 1,
            UnitPriceSnapshot = 50m,
            SubTotal = 50m,
            Currency = "BRL",
        };
        order.AddItem(item);
        db.CatalogOrders.Add(order);

        db.CatalogOrderItemResourceRequirements.Add(new CatalogOrderItemResourceRequirement
        {
            TenantId = tenant.Id,
            OrderItemId = item.Id,
            RentalAssetId = rental.Id,
            ResourceQuantity = 1,
            DurationMinutesPerUnit = 60,
            PrepMinutes = 0,
            CleanupMinutes = 0,
        });

        tenantProvider.TenantId = tenant.Id;
        await db.SaveChangesAsync();

        return new Seed(tenant.Id, unit.Id, customer.Id, asset.Id, rental.Id, product.Id, order.Id);
    }

    private static async Task<UpdateTenantRequestDto> BuildDisableResourcePlanningRequestAsync(
        PostgresAppDbFactory factory,
        Guid tenantId)
    {
        await using var db = factory.Create(new FakeTenantProvider { TenantId = null });
        var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId);
        return new UpdateTenantRequestDto
        {
            LegalName = tenant.LegalName,
            TaxId = tenant.TaxId,
            Subdomain = tenant.Subdomain ?? throw new InvalidOperationException("Subdomain is required."),
            ActiveModules = [PlatformModules.Catalog, PlatformModules.Rentals],
            AssetFamilyKeys = [Platform.Core.Domain.Constants.AssetFamilyKeys.Generic],
        };
    }

    private static async Task<UpdateOutcome> CaptureUpdateAsync(
        PostgresAppDbFactory factory,
        Guid tenantId,
        UpdateTenantRequestDto request)
    {
        var tenantProvider = new FakeTenantProvider { TenantId = Guid.NewGuid() };
        await using var db = factory.Create(tenantProvider);
        try
        {
            await CreateAdminService(db).UpdateAsync(tenantId, request, CancellationToken.None);
            return new UpdateOutcome(true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UpdateOutcome(false, ex);
        }
    }

    private static AdminTenantService CreateAdminService(AppDbContext db) =>
        new(
            db,
            new FakeTenantUserAdminService(),
            new FakePlatformAdminMembershipService(),
            new FakeTenantAccessBootstrapper(),
            NullLogger<AdminTenantService>.Instance,
            new TenantProvisioner(db, NullLogger<TenantProvisioner>.Instance),
            new TenantPurger(
                db,
                new FakeSupabaseAuthAdminClient(),
                Options.Create(new PlatformAdminOptions()),
                NullLogger<TenantPurger>.Instance,
                Options.Create(new StorageOptions()),
                new RecordingStorageProvider()));

    private static string UniqueTaxId() =>
        Random.Shared.NextInt64(10_000_000_000_000, 99_999_999_999_999).ToString();

    private static async Task<Capture<T>> CaptureAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return new Capture<T>(await action(), null);
        }
        catch (Exception ex)
        {
            return new Capture<T>(default, ex);
        }
    }

    private sealed record Seed(
        Guid TenantId,
        Guid UnitId,
        Guid CustomerId,
        Guid AssetId,
        Guid RentalAssetId,
        Guid ProductId,
        Guid OrderId);

    private sealed record Capture<T>(T? Value, Exception? Error);

    private sealed record UpdateOutcome(bool Succeeded, Exception? Error);
}
