using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;
using System.Text.Json;
using Platform.Api.Modules.Catalog.Controllers;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Modules.Rentals.Contracts;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Modules.ResourcePlanning;
using Platform.Api.Modules.ResourcePlanning.Dtos;
using Platform.Api.Modules.ResourcePlanning.Services;
using Platform.Api.Tests.Catalog;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.ResourcePlanning;

/// <summary>
/// Slice API-5: the catalog order flow allocates through the planning port. A tenant with catalog
/// and resource-planning and rentals OFF approves and allocates an order on an asset that is not
/// rentable (a 3D printer); the occupancy is an <c>AssetAllocation</c> of source <c>CatalogOrder</c>.
/// A rental-backed asset keeps its mirror reservation. The scenarios run in memory and, where the
/// asset-row lock or real constraints matter, on PostgreSQL.
/// </summary>
public sealed class CatalogOrderPlanningOnlyAllocationTests : IClassFixture<PostgresContainerFixture>
{
    // 2026-11-23 is a Monday; the tenant zone is Sao Paulo (UTC-3, no DST).
    private static readonly DateOnly Day = new(2026, 11, 23);
    private static readonly DateTimeOffset WorkStart = BrazilTimeZone.AtLocal(Day, new TimeOnly(10, 0));
    private static readonly DateTimeOffset WorkEnd = BrazilTimeZone.AtLocal(Day, new TimeOnly(11, 0));
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);

    private readonly PostgresContainerFixture _postgres;

    public CatalogOrderPlanningOnlyAllocationTests(PostgresContainerFixture postgres)
    {
        _postgres = postgres;
    }

    // ---- approve and allocate ----

    [Fact]
    public Task Approve_allocates_the_order_on_a_non_rentable_asset_with_rentals_off_in_memory() =>
        ApproveAllocatesOnNonRentableAsset(Rig.CreateInMemoryAsync());

    [DockerFact]
    public Task Approve_allocates_the_order_on_a_non_rentable_asset_with_rentals_off_on_postgres() =>
        ApproveAllocatesOnNonRentableAsset(Rig.CreatePostgresAsync(RequireFactory()));

    private static async Task ApproveAllocatesOnNonRentableAsset(Task<Rig> create)
    {
        var rig = await create;
        await using (var db = rig.NewDb())
        {
            await rig.Orders(db).ApproveAsync(rig.OrderId, rig.Planning(), default);
        }

        await using var verify = rig.NewDb();
        var order = await verify.CatalogOrders.AsNoTracking().SingleAsync(o => o.Id == rig.OrderId);
        Assert.Equal(CatalogOrderStatus.Approved, order.Status);

        var planning = await verify.AssetAllocations.AsNoTracking().SingleAsync();
        Assert.Equal(AssetAllocationSource.CatalogOrder, planning.Source);
        Assert.Equal(rig.AssetId, planning.AssetId);
        Assert.Equal(rig.ItemId, planning.CatalogOrderItemId);
        Assert.Equal("Pedido #1", planning.ReferenceLabel);
        Assert.Equal(AllocationStatus.Scheduled, planning.Status);
        Assert.Null(planning.ReservationItemId);
        Assert.Equal(rig.UnitId, planning.UnitId);
        Assert.Equal(WorkStart.AddMinutes(-15), planning.OccupiedStart);
        Assert.Equal(WorkEnd.AddMinutes(10), planning.OccupiedEnd);

        var row = await verify.CatalogOrderItemResourceAllocations.AsNoTracking().SingleAsync();
        Assert.Equal(planning.Id, row.AssetAllocationId);
        Assert.Null(row.ReservationItemId);
        Assert.Null(row.RentalAssetId);
        Assert.Equal(AllocationStatus.Scheduled, row.Status);
        Assert.Equal(WorkStart, row.PlannedWorkStart);

        // Rentals is off and the asset has no rental configuration: no reservation anywhere.
        Assert.Empty(await verify.Reservations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task The_allocation_shows_on_the_product_agenda_and_reads_the_stored_window()
    {
        var rig = await Rig.CreateInMemoryAsync();
        await using (var db = rig.NewDb())
        {
            await rig.Orders(db).ApproveAsync(rig.OrderId, rig.Planning(), default);
        }

        var query = new CatalogAgendaQuery(WorkStart.AddHours(-2), WorkEnd.AddHours(2), null, null);
        await using (var db = rig.NewDb())
        {
            var items = await new CatalogAgendaService(db, rig.Tenant).GetProductsAgendaAsync(query, default);
            var item = Assert.Single(items);
            Assert.Equal(rig.AssetId, item.AssetId);
            Assert.Null(item.RentalAssetId);
            Assert.Equal("Printer A", item.ResourceName);
            Assert.Equal(WorkStart.AddMinutes(-15), item.OccupiedStart);

            // The rentals resource agenda is keyed by rental asset: this allocation is not on it.
            var resources = await new CatalogAgendaService(db, rig.Tenant)
                .GetResourcesAgendaAsync(query, includeOrderDetails: true, default);
            Assert.Empty(resources.Items);
        }

        // The window is the stored one, not a recomputation from the requirement.
        await using (var db = rig.NewDb())
        {
            var planning = await db.AssetAllocations.SingleAsync();
            planning.OccupiedEnd = WorkEnd.AddHours(1);
            await db.SaveChangesAsync();
        }

        await using (var db = rig.NewDb())
        {
            var item = Assert.Single(await new CatalogAgendaService(db, rig.Tenant).GetProductsAgendaAsync(query, default));
            Assert.Equal(WorkEnd.AddHours(1), item.OccupiedEnd);
        }
    }

    [Fact]
    public async Task Preview_reports_feasible_then_the_conflict_on_a_non_rentable_asset()
    {
        var rig = await Rig.CreateInMemoryAsync();
        var request = new CatalogOrderAllocationPreviewRequest
        {
            UnitId = rig.UnitId,
            WorkStarts = [new AllocationPreviewWorkStart { OrderItemId = rig.ItemId, WorkStart = WorkStart }],
        };
        await using (var db = rig.NewDb())
        {
            var feasible = await rig.Preview(db).PreviewAsync(rig.OrderId, request, default);
            Assert.True(feasible.IsFeasible);
            Assert.Equal(rig.AssetId, feasible.AssetId);
            Assert.Null(feasible.RentalAssetId);
            Assert.Equal(WorkEnd, feasible.EstimatedReadyAt);
        }

        await using (var db = rig.NewDb())
        {
            await rig.Manual(db).CreateAsync(rig.ManualRequest(WorkStart, WorkEnd), default);
        }

        await using (var db = rig.NewDb())
        {
            var blocked = await rig.Preview(db).PreviewAsync(rig.OrderId, request, default);
            Assert.False(blocked.IsFeasible);
            Assert.Equal("resource_unavailable", blocked.ConflictCode);
            Assert.Contains("Printer A", blocked.Reason);
        }
    }

    // ---- conflicts with manual allocations ----

    [Fact]
    public async Task An_order_allocation_conflicts_with_a_manual_allocation_and_the_reverse()
    {
        var rig = await Rig.CreateInMemoryAsync();
        var secondOrder = await rig.AddOrderAsync(number: 2);

        await using (var db = rig.NewDb())
        {
            await rig.Orders(db).ApproveAsync(rig.OrderId, rig.Planning(), default);
        }

        // Reverse: a manual allocation over the order's window is refused.
        await using (var db = rig.NewDb())
        {
            var error = await Assert.ThrowsAsync<PlanningException>(
                () => rig.Manual(db).CreateAsync(rig.ManualRequest(WorkStart, WorkEnd), default));
            Assert.Equal(PlanningException.ConflictCode, error.Code);
            var conflict = Assert.Single(error.Conflicts!);
            Assert.Equal("allocation", conflict.Source);
            Assert.Contains("Pedido #1", conflict.Label);
        }

        // Another order over the same window is refused too, and stays Requested.
        await using (var db = rig.NewDb())
        {
            await Assert.ThrowsAsync<ResourceUnavailableException>(
                () => rig.Orders(db).ApproveAsync(secondOrder, rig.Planning(), default));
        }

        await using (var verify = rig.NewDb())
        {
            Assert.Equal(
                CatalogOrderStatus.Requested,
                (await verify.CatalogOrders.AsNoTracking().SingleAsync(o => o.Id == secondOrder)).Status);
            Assert.Single(await verify.AssetAllocations.AsNoTracking().ToListAsync());
        }

        // Forward: a manual allocation first blocks an order.
        var other = await Rig.CreateInMemoryAsync();
        await using (var db = other.NewDb())
        {
            await other.Manual(db).CreateAsync(other.ManualRequest(WorkStart, WorkEnd), default);
        }

        await using (var db = other.NewDb())
        {
            await Assert.ThrowsAsync<ResourceUnavailableException>(
                () => other.Orders(db).ApproveAsync(other.OrderId, other.Planning(), default));
        }
    }

    // ---- release ----

    [Fact]
    public Task Cancel_frees_an_unstarted_allocation_in_memory() =>
        CancelFreesTheAsset(Rig.CreateInMemoryAsync());

    [DockerFact]
    public Task Cancel_frees_an_unstarted_allocation_on_postgres() =>
        CancelFreesTheAsset(Rig.CreatePostgresAsync(RequireFactory()));

    private static async Task CancelFreesTheAsset(Task<Rig> create)
    {
        var rig = await create;
        await using (var db = rig.NewDb())
        {
            await rig.Orders(db).ApproveAsync(rig.OrderId, rig.Planning(), default);
        }

        await using (var db = rig.NewDb())
        {
            await rig.Orders(db).CancelAsync(rig.OrderId, "customer gave up", default);
        }

        await using var verify = rig.NewDb();
        var planning = await verify.AssetAllocations.AsNoTracking().SingleAsync();
        var row = await verify.CatalogOrderItemResourceAllocations.AsNoTracking().SingleAsync();
        Assert.Equal(AllocationStatus.Cancelled, planning.Status);
        Assert.Equal(AllocationStatus.Cancelled, row.Status);
        Assert.NotNull(planning.ActualReleaseAt);

        // The window is free again.
        var created = await rig.Manual(verify).CreateAsync(rig.ManualRequest(WorkStart, WorkEnd), default);
        Assert.Equal("Scheduled", created.Status);
    }

    [Fact]
    public async Task Cancelling_after_the_work_started_keeps_the_asset_occupied()
    {
        var rig = await Rig.CreateInMemoryAsync();
        await using (var db = rig.NewDb())
        {
            await rig.Orders(db).ApproveAsync(rig.OrderId, rig.Planning(), default);
        }

        await using (var db = rig.NewDb())
        {
            await rig.Orders(db).StartPreparingAsync(rig.OrderId, default);
        }

        await using (var db = rig.NewDb())
        {
            await rig.Orders(db).CancelAsync(rig.OrderId, "stopped", default);
        }

        await using var verify = rig.NewDb();
        Assert.Equal(AllocationStatus.Running, (await verify.AssetAllocations.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(
            AllocationStatus.Running,
            (await verify.CatalogOrderItemResourceAllocations.AsNoTracking().SingleAsync()).Status);
        await Assert.ThrowsAsync<PlanningException>(
            () => rig.Manual(verify).CreateAsync(rig.ManualRequest(WorkStart, WorkEnd), default));
    }

    // ---- usage ----

    [Fact]
    public Task Usage_start_finish_release_keep_both_records_in_step_in_memory() =>
        UsageKeepsBothRecordsInStep(Rig.CreateInMemoryAsync());

    [DockerFact]
    public Task Usage_start_finish_release_keep_both_records_in_step_on_postgres() =>
        UsageKeepsBothRecordsInStep(Rig.CreatePostgresAsync(RequireFactory()));

    private static async Task UsageKeepsBothRecordsInStep(Task<Rig> create)
    {
        var rig = await create;
        await using (var db = rig.NewDb())
        {
            await rig.Orders(db).ApproveAsync(rig.OrderId, rig.Planning(), default);
        }

        async Task Apply(string action)
        {
            await using var db = rig.NewDb();
            var row = await db.CatalogOrderItemResourceAllocations.AsNoTracking().SingleAsync();
            await rig.Usage(db).ApplyUsageActionAsync(
                rig.OrderId,
                row.Id,
                new CatalogOrderAllocationUsageRequest { Action = action, ExpectedVersion = row.PlanningRevision },
                default);
        }

        async Task<(AssetAllocation Planning, CatalogOrderItemResourceAllocation Row)> Read()
        {
            await using var db = rig.NewDb();
            return (
                await db.AssetAllocations.AsNoTracking().SingleAsync(),
                await db.CatalogOrderItemResourceAllocations.AsNoTracking().SingleAsync());
        }

        await Apply("start");
        var started = await Read();
        Assert.Equal(AllocationStatus.Running, started.Planning.Status);
        Assert.Equal(AllocationStatus.Running, started.Row.Status);
        Assert.NotNull(started.Planning.ActualStartAt);
        Assert.Equal(started.Row.ActualStartAt, started.Planning.ActualStartAt);

        // A started allocation keeps blocking.
        await using (var db = rig.NewDb())
        {
            await Assert.ThrowsAsync<PlanningException>(
                () => rig.Manual(db).CreateAsync(rig.ManualRequest(WorkStart, WorkEnd), default));
        }

        await Apply("work-finished");
        var finished = await Read();
        Assert.Equal(AllocationStatus.Cleaning, finished.Planning.Status);
        Assert.Equal(AllocationStatus.Cleaning, finished.Row.Status);
        Assert.NotNull(finished.Planning.ActualWorkFinishedAt);

        await Apply("release");
        var released = await Read();
        Assert.Equal(AllocationStatus.Released, released.Planning.Status);
        Assert.Equal(AllocationStatus.Released, released.Row.Status);
        Assert.NotNull(released.Planning.ActualReleaseAt);

        // Released: the asset is free again.
        await using var verify = rig.NewDb();
        var created = await rig.Manual(verify).CreateAsync(rig.ManualRequest(WorkStart, WorkEnd), default);
        Assert.Equal("Scheduled", created.Status);
    }

    // ---- duration ----

    [Fact]
    public async Task A_job_over_24_hours_fits_a_planning_only_asset_up_to_30_days()
    {
        var rig = await Rig.CreateInMemoryAsync(new RigOptions { DurationMinutes = 3000 });
        await using (var db = rig.NewDb())
        {
            await rig.Orders(db).ApproveAsync(rig.OrderId, rig.Planning(), default);
        }

        await using var verify = rig.NewDb();
        var planning = await verify.AssetAllocations.AsNoTracking().SingleAsync();
        Assert.Equal(TimeSpan.FromMinutes(3000), planning.PlannedWorkEnd - planning.PlannedWorkStart);

        var tooLong = await Rig.CreateInMemoryAsync(new RigOptions { DurationMinutes = 43_201 });
        await using var db2 = tooLong.NewDb();
        await Assert.ThrowsAsync<ArgumentException>(
            () => tooLong.Orders(db2).ApproveAsync(tooLong.OrderId, tooLong.Planning(), default));
    }

    [Fact]
    public async Task A_rental_backed_asset_keeps_the_one_day_limit()
    {
        var rig = await Rig.CreateInMemoryAsync(new RigOptions { RentalBacked = true, DurationMinutes = 1500 });
        await using var db = rig.NewDb();
        await Assert.ThrowsAsync<ArgumentException>(
            () => rig.Orders(db).ApproveAsync(rig.OrderId, rig.Planning(), default));
    }

    [Fact]
    public async Task Working_hours_apply_to_a_planning_only_asset_and_reject_with_422()
    {
        var rig = await Rig.CreateInMemoryAsync(new RigOptions { Policy = AssetHoursPolicy.StartWithinHours });
        await using var db = rig.NewDb();
        var error = await Assert.ThrowsAsync<CatalogPlanningRuleException>(() => rig.Orders(db).ApproveAsync(
            rig.OrderId,
            new CatalogOrderApprovePlanningRequest
            {
                UnitId = rig.UnitId,
                WorkStart = BrazilTimeZone.AtLocal(Day, new TimeOnly(20, 0)),
                ExpectedVersion = 0,
            },
            default));
        Assert.Equal(422, error.StatusCode);
        Assert.Equal(PlanningException.OutsideWorkingHoursCode, error.Code);

        // Inside the hours (Monday 08:00-18:00) it goes through.
        await rig.Orders(db).ApproveAsync(rig.OrderId, rig.Planning(), default);
    }

    // ---- rental-backed assets keep working through the port ----

    [Fact]
    public Task A_rental_backed_asset_still_gets_its_mirror_reservation_in_memory() =>
        RentalBackedKeepsTheMirror(Rig.CreateInMemoryAsync(new RigOptions { RentalBacked = true }));

    [DockerFact]
    public Task A_rental_backed_asset_still_gets_its_mirror_reservation_on_postgres() =>
        RentalBackedKeepsTheMirror(Rig.CreatePostgresAsync(RequireFactory(), new RigOptions { RentalBacked = true }));

    private static async Task RentalBackedKeepsTheMirror(Task<Rig> create)
    {
        var rig = await create;
        await using (var db = rig.NewDb())
        {
            await rig.Orders(db).ApproveAsync(rig.OrderId, rig.Planning(), default);
        }

        await using (var verify = rig.NewDb())
        {
            var planning = await verify.AssetAllocations.AsNoTracking().SingleAsync();
            var row = await verify.CatalogOrderItemResourceAllocations.AsNoTracking().SingleAsync();
            var item = await verify.ReservationItems.AsNoTracking().SingleAsync();
            Assert.Equal(item.Id, planning.ReservationItemId);
            Assert.Equal(item.Id, row.ReservationItemId);
            Assert.Equal(rig.RentalAssetId, row.RentalAssetId);
            Assert.Equal(planning.Id, row.AssetAllocationId);
            Assert.Equal(AssetAllocationSource.CatalogOrder, planning.Source);
            var reservation = await verify.Reservations.AsNoTracking().SingleAsync();
            Assert.Equal(ReservationPurpose.InternalUse, reservation.Purpose);
            Assert.Equal(ReservationStatus.Confirmed, reservation.Status);
            Assert.Equal(planning.OccupiedStart, reservation.StartDateTime);
        }

        await using (var db = rig.NewDb())
        {
            await rig.Orders(db).CancelAsync(rig.OrderId, "no", default);
        }

        await using (var verify = rig.NewDb())
        {
            Assert.Equal(ReservationStatus.Canceled, (await verify.Reservations.AsNoTracking().SingleAsync()).Status);
            Assert.Equal(AllocationStatus.Cancelled, (await verify.AssetAllocations.AsNoTracking().SingleAsync()).Status);
            Assert.Equal(
                AllocationStatus.Cancelled,
                (await verify.CatalogOrderItemResourceAllocations.AsNoTracking().SingleAsync()).Status);
        }
    }

    // ---- modules ----

    [Fact]
    public async Task Approving_with_a_required_module_off_returns_403_module_inactive_not_a_500()
    {
        var rig = await Rig.CreateInMemoryAsync();
        await using (var db = rig.NewDb())
        {
            var planningModule = await db.TenantModules.SingleAsync(
                m => m.TenantId == rig.TenantId && m.ModuleName == PlatformModules.ResourcePlanning);
            planningModule.Deactivate();
            await db.SaveChangesAsync();
        }

        await using var attempt = rig.NewDb();
        var typed = await Assert.ThrowsAsync<RequiredModuleInactiveException>(
            () => rig.Orders(attempt).ApproveAsync(rig.OrderId, rig.Planning(), default));
        Assert.Contains(PlatformModules.ResourcePlanning, typed.Modules);

        var result = await rig.Controller(attempt).Approve(rig.OrderId, rig.Planning(), default);
        var response = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(403, response.StatusCode);
        var body = JsonSerializer.SerializeToElement(response.Value);
        Assert.Equal("module_inactive", body.GetProperty("code").GetString());
        Assert.Equal(PlatformModules.ResourcePlanning, body.GetProperty("module").GetString());

        await using var verify = rig.NewDb();
        Assert.Empty(await verify.AssetAllocations.AsNoTracking().ToListAsync());
        Assert.Equal(
            CatalogOrderStatus.Requested,
            (await verify.CatalogOrders.AsNoTracking().SingleAsync(o => o.Id == rig.OrderId)).Status);
    }

    [Fact]
    public async Task Rentals_is_not_required_to_allocate_on_a_non_rentable_asset()
    {
        // Same rig, no rentals module row at all: the approve above already ran without it.
        var rig = await Rig.CreateInMemoryAsync();
        await using var db = rig.NewDb();
        Assert.DoesNotContain(
            await db.TenantModules.AsNoTracking().ToListAsync(),
            m => m.ModuleName == PlatformModules.Rentals);
        await rig.Orders(db).ApproveAsync(rig.OrderId, rig.Planning(), default);
    }

    // ---- requirement API ----

    [Fact]
    public async Task Requirement_api_accepts_asset_id_and_keeps_rental_asset_id_for_rental_backed_assets()
    {
        var rig = await Rig.CreateInMemoryAsync();
        await using (var db = rig.NewDb())
        {
            var service = new CatalogProductResourceRequirementService(db, rig.Tenant);
            var saved = await service.ReplaceAsync(
                rig.ProductId,
                Replace(0, new CatalogProductResourceRequirementDto
                {
                    AssetId = rig.AssetId,
                    ResourceQuantity = 1,
                    DurationMinutesPerUnit = 3000,
                }),
                default);
            var row = Assert.Single(saved.Requirements);
            Assert.Equal(rig.AssetId, row.AssetId);
            Assert.Null(row.RentalAssetId);
            Assert.Equal(3000, row.DurationMinutesPerUnit);
        }

        var rental = await Rig.CreateInMemoryAsync(new RigOptions { RentalBacked = true });
        await using (var db = rental.NewDb())
        {
            var service = new CatalogProductResourceRequirementService(db, rental.Tenant);

            // By asset id: the rental asset is filled in.
            var byAsset = await service.ReplaceAsync(
                rental.ProductId,
                Replace(0, new CatalogProductResourceRequirementDto
                {
                    AssetId = rental.AssetId,
                    ResourceQuantity = 1,
                    DurationMinutesPerUnit = 60,
                }),
                default);
            var first = Assert.Single(byAsset.Requirements);
            Assert.Equal(rental.AssetId, first.AssetId);
            Assert.Equal(rental.RentalAssetId, first.RentalAssetId);

            // By the legacy rental asset id: the asset is filled in.
            var byRental = await service.ReplaceAsync(
                rental.ProductId,
                Replace(1, new CatalogProductResourceRequirementDto
                {
                    RentalAssetId = rental.RentalAssetId,
                    ResourceQuantity = 1,
                    DurationMinutesPerUnit = 60,
                }),
                default);
            var second = Assert.Single(byRental.Requirements);
            Assert.Equal(rental.AssetId, second.AssetId);
            Assert.Equal(rental.RentalAssetId, second.RentalAssetId);

            // Both, naming different resources, is refused; so is a rental-backed job over one day.
            await Assert.ThrowsAsync<ArgumentException>(() => service.ReplaceAsync(
                rental.ProductId,
                Replace(2, new CatalogProductResourceRequirementDto
                {
                    AssetId = rental.SecondAssetId,
                    RentalAssetId = rental.RentalAssetId,
                    ResourceQuantity = 1,
                    DurationMinutesPerUnit = 60,
                }),
                default));
            await Assert.ThrowsAsync<ArgumentException>(() => service.ReplaceAsync(
                rental.ProductId,
                Replace(2, new CatalogProductResourceRequirementDto
                {
                    AssetId = rental.AssetId,
                    ResourceQuantity = 1,
                    DurationMinutesPerUnit = 1500,
                }),
                default));
        }
    }

    [Fact]
    public async Task Requirement_api_refuses_an_asset_that_is_not_plannable_and_another_tenants_asset()
    {
        var rig = await Rig.CreateInMemoryAsync();
        await using var db = rig.NewDb();

        var profile = await db.AssetPlanningProfiles.SingleAsync(p => p.AssetId == rig.SecondAssetId);
        profile.IsPlannable = false;
        var other = PlanningTestData.AddTenant(db, name: "Other Shop");
        await db.SaveChangesAsync();

        var service = new CatalogProductResourceRequirementService(db, rig.Tenant);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReplaceAsync(
            rig.ProductId,
            Replace(0, new CatalogProductResourceRequirementDto
            {
                AssetId = rig.SecondAssetId,
                ResourceQuantity = 1,
                DurationMinutesPerUnit = 60,
            }),
            default));

        // Another tenant's asset is a 404, never a hint that it exists.
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ReplaceAsync(
            rig.ProductId,
            Replace(0, new CatalogProductResourceRequirementDto
            {
                AssetId = other.AssetId,
                ResourceQuantity = 1,
                DurationMinutesPerUnit = 60,
            }),
            default));
        Assert.Empty(await db.CatalogProductResourceRequirements.AsNoTracking().ToListAsync());
    }

    // ---- PostgreSQL: tenant isolation, backfill and the Down guard ----

    [DockerFact]
    public async Task Two_tenants_allocate_the_same_window_on_their_own_assets_without_seeing_each_other()
    {
        var factory = RequireFactory();
        var first = await Rig.CreatePostgresAsync(factory);
        var second = await Rig.CreatePostgresAsync(factory);

        foreach (var rig in new[] { first, second })
        {
            await using var db = rig.NewDb();
            await rig.Orders(db).ApproveAsync(rig.OrderId, rig.Planning(), default);
        }

        foreach (var rig in new[] { first, second })
        {
            await using var verify = rig.NewDb();
            var planning = await verify.AssetAllocations.AsNoTracking().SingleAsync();
            Assert.Equal(rig.TenantId, planning.TenantId);
            Assert.Equal(rig.AssetId, planning.AssetId);
        }
    }

    [DockerFact]
    public async Task Backfill_links_a_phase_0_allocation_to_a_planning_record_and_is_idempotent()
    {
        var factory = RequireFactory();
        var rigA = await Rig.CreatePostgresAsync(
            factory, new RigOptions { RentalBacked = true, PrepMinutes = 15, CleanupMinutes = 10 });
        var rigB = await Rig.CreatePostgresAsync(
            factory, new RigOptions { RentalBacked = true, PrepMinutes = 0, CleanupMinutes = 0 });
        var phase0 = new List<(Rig Rig, Guid ReservationItemId, Guid RowId)>();

        // A Phase 0 row: reservation written by the rentals port, catalog allocation with no link,
        // requirement with the rental asset only.
        foreach (var rig in new[] { rigA, rigB })
        {
            await using var db = rig.NewDb();
            var reservationItemId = await new ResourceOccupancyService(new TestTimeProvider(Now))
                .CreateInternalUseOccupancyAsync(
                    db, rig.TenantId, rig.UnitId, rig.RentalAssetId, 1,
                    WorkStart.AddMinutes(-rig.PrepMinutes), WorkEnd.AddMinutes(rig.CleanupMinutes), default);
            var requirement = await db.CatalogOrderItemResourceRequirements.SingleAsync();
            Assert.Null(requirement.AssetId);
            var row = new CatalogOrderItemResourceAllocation
            {
                TenantId = rig.TenantId,
                RequirementId = requirement.Id,
                ReservationItemId = reservationItemId,
                Sequence = 1,
                PlanningRevision = 3,
                AllocatedQuantity = 1,
                RentalAssetId = rig.RentalAssetId,
                PlannedWorkStart = WorkStart,
                PlannedWorkEnd = WorkEnd,
                TimeZoneId = "America/Sao_Paulo",
                Status = AllocationStatus.Running,
                ActualStartAt = WorkStart.AddMinutes(5),
            };
            db.CatalogOrderItemResourceAllocations.Add(row);
            await db.SaveChangesAsync();
            phase0.Add((rig, reservationItemId, row.Id));
        }

        await using (var db = rigA.NewDb())
        {
            await db.Database.ExecuteSqlRawAsync(Platform.Core.Infrastructure.Persistence.Migrations
                .LinkCatalogAllocationsToAssetAllocations.BackfillSql);
            // A second run inserts and links nothing.
            await db.Database.ExecuteSqlRawAsync(Platform.Core.Infrastructure.Persistence.Migrations
                .LinkCatalogAllocationsToAssetAllocations.BackfillSql);
        }

        foreach (var (rig, reservationItemId, rowId) in phase0)
        {
            await using var verify = rig.NewDb();
            var row = await verify.CatalogOrderItemResourceAllocations.AsNoTracking().SingleAsync(a => a.Id == rowId);
            var planning = await verify.AssetAllocations.AsNoTracking().SingleAsync();
            Assert.Equal(planning.Id, row.AssetAllocationId);
            Assert.Equal(rig.TenantId, planning.TenantId);
            Assert.Equal(rig.AssetId, planning.AssetId);
            Assert.Equal(rig.UnitId, planning.UnitId);
            Assert.Equal(AssetAllocationSource.CatalogOrder, planning.Source);
            Assert.Equal(AssetAllocationKind.Use, planning.Kind);
            Assert.Equal(AllocationStatus.Running, planning.Status);
            Assert.Equal(WorkStart.AddMinutes(5), planning.ActualStartAt);
            Assert.Equal(reservationItemId, planning.ReservationItemId);
            Assert.Equal(rig.ItemId, planning.CatalogOrderItemId);
            Assert.Equal("Pedido #1", planning.ReferenceLabel);
            Assert.Equal(WorkStart, planning.PlannedWorkStart);
            Assert.Equal(WorkEnd, planning.PlannedWorkEnd);
            Assert.Equal(WorkStart.AddMinutes(-rig.PrepMinutes), planning.OccupiedStart);
            Assert.Equal(WorkEnd.AddMinutes(rig.CleanupMinutes), planning.OccupiedEnd);
            Assert.Equal("America/Sao_Paulo", planning.TimeZoneId);

            var requirement = await verify.CatalogOrderItemResourceRequirements.AsNoTracking().SingleAsync();
            Assert.Equal(rig.AssetId, requirement.AssetId);
            Assert.Equal(rig.RentalAssetId, requirement.RentalAssetId);
        }

        // Nothing was deleted: the reservation and the catalog rows are all still there.
        await using var final = rigA.NewDb();
        Assert.Single(await final.CatalogOrderItemResourceAllocations.AsNoTracking().ToListAsync());
        Assert.Single(await final.ReservationItems.AsNoTracking().ToListAsync());
    }

    [DockerFact]
    public async Task Down_refuses_while_a_planning_only_allocation_exists_and_allows_rental_backed_rows()
    {
        var factory = RequireFactory();
        var planningOnly = await Rig.CreatePostgresAsync(factory);
        await using (var db = planningOnly.NewDb())
        {
            await planningOnly.Orders(db).ApproveAsync(planningOnly.OrderId, planningOnly.Planning(), default);
        }

        var migration = new Platform.Core.Infrastructure.Persistence.Migrations.LinkCatalogAllocationsToAssetAllocations();
        var guard = Assert.IsType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>(
            migration.DownOperations[0]);
        Assert.Equal(
            Platform.Core.Infrastructure.Persistence.Migrations.LinkCatalogAllocationsToAssetAllocations.DownGuardSql,
            guard.Sql);

        await using var db2 = planningOnly.NewDb();
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db2.Database.ExecuteSqlRawAsync(guard.Sql));
        Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException, error.SqlState);
        Assert.Contains("Cannot roll back catalog allocation links", error.MessageText);

        // With only rental-backed rows (every other row removed inside a transaction that is rolled
        // back) the guard lets the rollback proceed.
        await using var db3 = planningOnly.NewDb();
        await using var transaction = await db3.Database.BeginTransactionAsync();
        await db3.Database.ExecuteSqlRawAsync("DELETE FROM catalog.order_item_resource_allocations");
        await db3.Database.ExecuteSqlRawAsync("DELETE FROM catalog.order_item_resource_requirements");
        await db3.Database.ExecuteSqlRawAsync("DELETE FROM catalog.product_resource_requirements");
        await db3.Database.ExecuteSqlRawAsync(guard.Sql);
        await transaction.RollbackAsync();
    }

    [Fact]
    public void The_migration_is_additive_and_its_down_guard_runs_first()
    {
        var migration = new Platform.Core.Infrastructure.Persistence.Migrations.LinkCatalogAllocationsToAssetAllocations();

        Assert.DoesNotContain(
            migration.UpOperations,
            operation => operation is Microsoft.EntityFrameworkCore.Migrations.Operations.DropTableOperation
                or Microsoft.EntityFrameworkCore.Migrations.Operations.DropColumnOperation
                or Microsoft.EntityFrameworkCore.Migrations.Operations.DeleteDataOperation);
        var sql = migration.UpOperations
            .OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>()
            .Select(o => o.Sql)
            .Single();
        Assert.Equal(
            Platform.Core.Infrastructure.Persistence.Migrations.LinkCatalogAllocationsToAssetAllocations.BackfillSql,
            sql);
        foreach (var forbidden in new[] { "DELETE FROM", "TRUNCATE", "DROP TABLE", "DROP COLUMN" })
        {
            Assert.DoesNotContain(forbidden, sql, StringComparison.OrdinalIgnoreCase);
        }

        // Both NOT NULL columns of the catalog allocation are relaxed, nothing else is altered.
        var altered = migration.UpOperations
            .OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.AlterColumnOperation>()
            .ToList();
        Assert.Equal(
            ["rental_asset_id", "reservation_item_id"],
            altered.Select(o => o.Name).Order().ToArray());
        Assert.All(altered, o => Assert.True(o.IsNullable));

        Assert.IsType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>(migration.DownOperations[0]);
    }

    private PostgresAppDbFactory RequireFactory()
    {
        Assert.NotNull(_postgres.Factory);
        return _postgres.Factory!;
    }

    private static ReplaceCatalogProductResourceRequirementsRequest Replace(
        int expectedVersion,
        params CatalogProductResourceRequirementDto[] rows) =>
        new() { ExpectedVersion = expectedVersion, Requirements = rows };

    internal sealed record RigOptions
    {
        public bool RentalBacked { get; init; }

        public int DurationMinutes { get; init; } = 60;

        public int PrepMinutes { get; init; } = 15;

        public int CleanupMinutes { get; init; } = 10;

        public AssetHoursPolicy Policy { get; init; } = AssetHoursPolicy.Anytime;
    }

    /// <summary>One tenant with catalog and resource-planning, a plannable asset and one order.</summary>
    internal sealed class Rig
    {
        private readonly Func<AppDbContext> _newDb;
        private readonly RigOptions _options;
        private readonly TestTimeProvider _clock = new(Now);

        private Rig(
            Func<AppDbContext> newDb,
            FakeTenantProvider tenant,
            RigOptions options,
            PlanningTenantSeed seed,
            Guid productId,
            Guid orderId,
            Guid itemId,
            Guid rentalAssetId)
        {
            _newDb = newDb;
            Tenant = tenant;
            _options = options;
            TenantId = seed.TenantId;
            UnitId = seed.UnitId;
            AssetId = seed.AssetId;
            SecondAssetId = seed.SecondAssetId;
            ProductId = productId;
            OrderId = orderId;
            ItemId = itemId;
            RentalAssetId = rentalAssetId;
        }

        public FakeTenantProvider Tenant { get; }

        public Guid TenantId { get; }

        public Guid UnitId { get; }

        public Guid AssetId { get; }

        public Guid SecondAssetId { get; }

        public Guid ProductId { get; }

        public Guid OrderId { get; }

        public Guid ItemId { get; }

        public Guid RentalAssetId { get; }

        public int PrepMinutes => _options.PrepMinutes;

        public int CleanupMinutes => _options.CleanupMinutes;

        public AppDbContext NewDb() => _newDb();

        public static Task<Rig> CreateInMemoryAsync(RigOptions? options = null)
        {
            var tenant = new FakeTenantProvider();
            var name = $"order-planning-{Guid.NewGuid():N}";
            return CreateAsync(() => InMemoryAppDb.Create(tenant, name), tenant, options ?? new RigOptions());
        }

        public static Task<Rig> CreatePostgresAsync(PostgresAppDbFactory factory, RigOptions? options = null)
        {
            var tenant = new FakeTenantProvider();
            return CreateAsync(() => factory.Create(tenant), tenant, options ?? new RigOptions());
        }

        private static async Task<Rig> CreateAsync(
            Func<AppDbContext> newDb,
            FakeTenantProvider tenant,
            RigOptions options)
        {
            await using var db = newDb();
            var seed = PlanningTestData.AddTenant(
                db,
                policy: options.Policy,
                withPlanningModule: true,
                withRentalsModule: options.RentalBacked);
            db.TenantModules.Add(new TenantModule(seed.TenantId, PlatformModules.Catalog, isActive: true));

            if (options.Policy != AssetHoursPolicy.Anytime)
            {
                db.AssetWorkingHours.Add(PlanningTestData.Hours(seed.TenantId, seed.AssetId, DayOfWeek.Monday, 8, 18));
            }

            var rentalAssetId = Guid.Empty;
            if (options.RentalBacked)
            {
                var rental = new RentalAsset
                {
                    TenantId = seed.TenantId,
                    AssetId = seed.AssetId,
                    Type = RentalAssetType.Location,
                    TotalQuantity = 1,
                    IsActive = true,
                    RequiresDeposit = false,
                    SchedulePolicy = SchedulePolicy.OpenHours,
                    OpenTime = new TimeOnly(8, 0),
                    CloseTime = new TimeOnly(22, 0),
                    QueueEnabled = false,
                };
                db.RentalAssets.Add(rental);
                db.Assets.Local.Single(a => a.Id == seed.AssetId).IsRentable = true;
                rentalAssetId = rental.Id;
            }

            var customer = new Customer
            {
                TenantId = seed.TenantId,
                Name = "Ana",
                Email = $"{Guid.NewGuid():N}@shop.test",
                Phone = "11999999999",
            };
            var product = new CatalogProduct
            {
                TenantId = seed.TenantId,
                Name = "Impressao 3D",
                Price = 50m,
                Currency = "BRL",
                IsActive = true,
            };
            db.Customers.Add(customer);
            db.CatalogProducts.Add(product);
            var (order, item) = AddOrder(db, seed.TenantId, customer, product, number: 1);
            AddRequirement(db, seed.TenantId, item.Id, options, seed.AssetId, rentalAssetId);

            tenant.TenantId = seed.TenantId;
            await db.SaveChangesAsync();

            return new Rig(newDb, tenant, options, seed, product.Id, order.Id, item.Id, rentalAssetId);
        }

        public async Task<Guid> AddOrderAsync(int number)
        {
            await using var db = NewDb();
            var customer = await db.Customers.AsNoTracking().FirstAsync();
            var product = await db.CatalogProducts.AsNoTracking().FirstAsync();
            var (order, item) = AddOrder(db, TenantId, customer, product, number);
            AddRequirement(db, TenantId, item.Id, _options, AssetId, RentalAssetId);
            await db.SaveChangesAsync();
            return order.Id;
        }

        private static (CatalogOrder Order, CatalogOrderItem Item) AddOrder(
            AppDbContext db,
            Guid tenantId,
            Customer customer,
            CatalogProduct product,
            int number)
        {
            var order = new CatalogOrder
            {
                TenantId = tenantId,
                CustomerId = customer.Id,
                OrderNumber = number,
                CustomerNameSnapshot = customer.Name,
                Currency = "BRL",
            };
            var item = new CatalogOrderItem
            {
                TenantId = tenantId,
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
            return (order, item);
        }

        private static void AddRequirement(
            AppDbContext db,
            Guid tenantId,
            Guid itemId,
            RigOptions options,
            Guid assetId,
            Guid rentalAssetId) =>
            db.CatalogOrderItemResourceRequirements.Add(new CatalogOrderItemResourceRequirement
            {
                TenantId = tenantId,
                OrderItemId = itemId,

                // A rental-backed requirement is the Phase 0 shape (the rental asset only); a
                // planning-only one names the asset.
                AssetId = options.RentalBacked ? null : assetId,
                RentalAssetId = options.RentalBacked ? rentalAssetId : null,
                ResourceQuantity = 1,
                DurationMinutesPerUnit = options.DurationMinutes,
                PrepMinutes = options.PrepMinutes,
                CleanupMinutes = options.CleanupMinutes,
            });

        public CatalogOrderApprovePlanningRequest Planning() =>
            new() { UnitId = UnitId, WorkStart = WorkStart, ExpectedVersion = 0 };

        public CreateAssetAllocationRequest ManualRequest(DateTimeOffset start, DateTimeOffset end) =>
            new() { AssetId = AssetId, Kind = "Use", Title = "Calibration", WorkStart = start, WorkEnd = end };

        public CatalogOrderService Orders(AppDbContext db)
        {
            var occupancy = new ResourceOccupancyService(_clock);
            var publisher = new CatalogNotificationPublisher(
                db, Tenant, new FakePermissionResolver(), NullLogger<CatalogNotificationPublisher>.Instance);
            var scheduler = new RecordingOutboxScheduler();
            var http = new FakeHttpContextAccessor();
            var allocations = new CatalogOrderAllocationService(
                db, Tenant, occupancy, publisher.AsDomainEvents(), scheduler, http);
            return new CatalogOrderService(
                db, Tenant, http, publisher.AsDomainEvents(), scheduler, allocations, occupancy);
        }

        public CatalogOrderAllocationUsageService Usage(AppDbContext db) =>
            new(db, Tenant, new ResourceOccupancyService(_clock));

        public CatalogOrderAllocationPreviewService Preview(AppDbContext db) =>
            new(db, Tenant, new ResourceOccupancyService(_clock));

        public AssetAllocationService Manual(AppDbContext db)
        {
            var rentals = new ResourceOccupancyService(_clock);
            var accessor = _options.RentalBacked
                ? new FixedModuleAccessor(PlatformModules.ResourcePlanning, PlatformModules.Rentals)
                : new FixedModuleAccessor(PlatformModules.ResourcePlanning);
            return new AssetAllocationService(
                db,
                Tenant,
                new AssetOccupancyService(db, accessor, rentals, _clock),
                new FakePlanningCaller(Guid.NewGuid(), canManage: true),
                _clock);
        }

        public CatalogOrdersController Controller(AppDbContext db)
        {
            var occupancy = new ResourceOccupancyService(_clock);
            var publisher = new CatalogNotificationPublisher(
                db, Tenant, new FakePermissionResolver(), NullLogger<CatalogNotificationPublisher>.Instance);
            return new CatalogOrdersController(
                Orders(db),
                new CatalogOrderAllocationService(
                    db, Tenant, occupancy, publisher.AsDomainEvents(), new RecordingOutboxScheduler(), new FakeHttpContextAccessor()),
                Preview(db),
                Usage(db),
                new AllowAllAuthorization());
        }
    }

    private sealed class AllowAllAuthorization : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(AuthorizationResult.Success());

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Success());
    }
}
