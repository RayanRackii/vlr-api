using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Notifications;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.Catalog;

/// <summary>
/// Real-PostgreSQL coverage for the read-only Catalog agenda (CatalogAgendaService) and the
/// staff-actor attribution added to order approval. Needs a real database because the agenda
/// reads span the allocation, requirement, order-item, order and asset tables through the live
/// tenant query filters, and the actor flow runs the full approve-with-planning transaction.
/// </summary>
public sealed class CatalogAgendaTests : IClassFixture<PostgresContainerFixture>
{
    private static readonly DateOnly Date = new(2026, 9, 1);
    private static readonly TimeOnly WorkStartLocal = new(10, 0);

    private readonly PostgresContainerFixture _postgres;

    public CatalogAgendaTests(PostgresContainerFixture postgres)
    {
        _postgres = postgres;
    }

    [DockerFact]
    public async Task Products_agenda_includes_allocation_overlapping_only_via_buffer_and_excludes_outside_range()
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant, prepMinutes: 30, cleanupMinutes: 30);
        // Insert the allocation directly rather than through approve: the buffered occupied window
        // (120 min) is intentionally longer than the OpenHours policy allows for a single booking,
        // and this test is about the agenda's buffer-overlap maths, not work-window admission.
        await InsertAllocationAsync(factory, tenant, seed);

        await using var db = factory.Create(tenant);
        var service = new CatalogAgendaService(db, tenant);

        // Work window is [10:00, 11:00); the 30-min prep/cleanup buffers push the occupied window
        // out to [09:30, 11:30). A range touching only the prep buffer must still match.
        var bufferOnly = await service.GetProductsAgendaAsync(
            Query(new TimeOnly(9, 0), new TimeOnly(9, 45)), CancellationToken.None);
        Assert.Single(bufferOnly);
        var item = bufferOnly[0];
        Assert.Equal(Local(new TimeOnly(10, 0)), item.WorkStart);
        Assert.Equal(Local(new TimeOnly(9, 30)), item.OccupiedStart);
        Assert.Equal(Local(new TimeOnly(11, 30)), item.OccupiedEnd);
        Assert.Equal(AllocationStatus.Scheduled, item.AllocationStatus);

        var outside = await service.GetProductsAgendaAsync(
            Query(new TimeOnly(12, 0), new TimeOnly(13, 0)), CancellationToken.None);
        Assert.Empty(outside);
    }

    [DockerFact]
    public async Task Products_agenda_accepts_non_utc_offsets()
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant, prepMinutes: 30, cleanupMinutes: 30);
        await InsertAllocationAsync(factory, tenant, seed);

        await using var db = factory.Create(tenant);
        var service = new CatalogAgendaService(db, tenant);
        var utc = Query(new TimeOnly(9, 0), new TimeOnly(9, 45));
        var offset = TimeSpan.FromHours(-3);

        var items = await service.GetProductsAgendaAsync(
            utc with { From = utc.From.ToOffset(offset), To = utc.To.ToOffset(offset) },
            CancellationToken.None);

        Assert.Single(items);
    }

    [DockerFact]
    public async Task Products_agenda_is_tenant_scoped()
    {
        var factory = RequireFactory();
        var tenantA = new FakeTenantProvider();
        var tenantB = new FakeTenantProvider();
        var seedA = await SeedAsync(factory, tenantA, prepMinutes: 0, cleanupMinutes: 0);
        var seedB = await SeedAsync(factory, tenantB, prepMinutes: 0, cleanupMinutes: 0);
        await ApproveAsync(factory, tenantA, seedA, httpUser: null);
        await ApproveAsync(factory, tenantB, seedB, httpUser: null);

        await using var db = factory.Create(tenantA);
        var service = new CatalogAgendaService(db, tenantA);

        var items = await service.GetProductsAgendaAsync(
            Query(new TimeOnly(9, 0), new TimeOnly(12, 0)), CancellationToken.None);

        Assert.Single(items);
        Assert.Equal(seedA.OrderId, items[0].OrderId);
    }

    [DockerFact]
    public async Task Resources_agenda_hides_customer_name_and_display_number_without_order_details()
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant, prepMinutes: 0, cleanupMinutes: 0);
        await ApproveAsync(factory, tenant, seed, httpUser: null);

        await using var db = factory.Create(tenant);
        var service = new CatalogAgendaService(db, tenant);

        var hidden = await service.GetResourcesAgendaAsync(
            Query(new TimeOnly(9, 0), new TimeOnly(12, 0)), includeOrderDetails: false, CancellationToken.None);
        Assert.Single(hidden.Items);
        Assert.Null(hidden.Items[0].CustomerName);
        Assert.Null(hidden.Items[0].DisplayNumber);
        Assert.NotEmpty(hidden.Resources);
        Assert.Contains(hidden.Resources, r => r.Id == seed.RentalAssetId);

        var shown = await service.GetResourcesAgendaAsync(
            Query(new TimeOnly(9, 0), new TimeOnly(12, 0)), includeOrderDetails: true, CancellationToken.None);
        Assert.Equal("Ana", shown.Items[0].CustomerName);
        Assert.Equal("#1", shown.Items[0].DisplayNumber);
    }

    [DockerFact]
    public async Task Approve_attributes_history_to_authenticated_staff_user_with_name()
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant, prepMinutes: 0, cleanupMinutes: 0);

        User staff;
        await using (var setup = factory.Create(tenant))
        {
            staff = new User(seed.TenantId, Guid.NewGuid().ToString("N"), "Carlos Operador", $"{Guid.NewGuid():N}@ops.test");
            setup.Users.Add(staff);
            await setup.SaveChangesAsync();
        }

        await ApproveAsync(factory, tenant, seed, httpUser: staff);

        await using var db = factory.Create(tenant);
        var orders = CreateOrders(db, tenant, httpUser: null);
        var order = await orders.GetByIdAsync(seed.OrderId, CancellationToken.None);

        Assert.NotNull(order);
        var approved = order!.History.Single(h => h.Status == CatalogOrderStatus.Approved);
        Assert.Equal(CatalogActorType.B2BUser, approved.ActorType);
        Assert.Equal(staff.Id, approved.ActorId);
        Assert.Equal("Carlos Operador", approved.ActorName);
    }

    private static CatalogAgendaQuery Query(TimeOnly from, TimeOnly to) =>
        new(Local(from), Local(to), ProductId: null, RentalAssetId: null);

    private static DateTimeOffset Local(TimeOnly time) => BrazilTimeZone.AtLocal(Date, time);

    private PostgresAppDbFactory RequireFactory()
    {
        Assert.NotNull(_postgres.Factory);
        return _postgres.Factory!;
    }

    private static async Task ApproveAsync(
        PostgresAppDbFactory factory,
        FakeTenantProvider tenant,
        Seed seed,
        User? httpUser)
    {
        await using var db = factory.Create(tenant);
        var orders = CreateOrders(db, tenant, httpUser);
        await orders.ApproveAsync(
            seed.OrderId,
            new CatalogOrderApprovePlanningRequest { UnitId = seed.UnitId, WorkStart = Local(WorkStartLocal), ExpectedVersion = 0 },
            CancellationToken.None);
    }

    private static async Task InsertAllocationAsync(
        PostgresAppDbFactory factory,
        FakeTenantProvider tenant,
        Seed seed)
    {
        await using var db = factory.Create(tenant);
        var occupancy = new ResourceOccupancyService(
            new TestTimeProvider(BrazilTimeZone.AtLocal(Date, new TimeOnly(9, 0))));
        var reservationItemId = await occupancy.CreateInternalUseOccupancyAsync(
            db, seed.TenantId, seed.UnitId, seed.RentalAssetId, 1,
            Local(new TimeOnly(9, 30)), Local(new TimeOnly(11, 30)), CancellationToken.None);

        var requirement = await db.CatalogOrderItemResourceRequirements
            .FirstAsync(r => r.TenantId == seed.TenantId);
        db.CatalogOrderItemResourceAllocations.Add(new CatalogOrderItemResourceAllocation
        {
            TenantId = seed.TenantId,
            RequirementId = requirement.Id,
            ReservationItemId = reservationItemId,
            Sequence = 1,
            PlanningRevision = 1,
            AllocatedQuantity = 1,
            RentalAssetId = seed.RentalAssetId,
            PlannedWorkStart = Local(new TimeOnly(10, 0)),
            PlannedWorkEnd = Local(new TimeOnly(11, 0)),
            TimeZoneId = "America/Sao_Paulo",
            Status = AllocationStatus.Scheduled,
        });
        await db.SaveChangesAsync();
    }

    private static CatalogOrderService CreateOrders(AppDbContext db, FakeTenantProvider tenant, User? httpUser)
    {
        var publisher = new CatalogNotificationPublisher(
            db, tenant, new FakePermissionResolver(), NullLogger<CatalogNotificationPublisher>.Instance);
        var occupancy = new ResourceOccupancyService(
            new TestTimeProvider(BrazilTimeZone.AtLocal(Date, new TimeOnly(9, 0))));
        var scheduler = new RecordingOutboxScheduler();
        var http = new FakeHttpContextAccessor();
        if (httpUser is not null)
        {
            var identity = new ClaimsIdentity(
                [new Claim("sub", httpUser.SupabaseAuthId)], authenticationType: "Test");
            http.HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity),
            };
        }

        var allocationService = new CatalogOrderAllocationService(
            db, tenant, occupancy, publisher.AsDomainEvents(), scheduler, http);
        return new CatalogOrderService(
            db, tenant, http, publisher.AsDomainEvents(), scheduler, allocationService, occupancy);
    }

    private static async Task<Seed> SeedAsync(
        PostgresAppDbFactory factory,
        FakeTenantProvider tenant,
        int prepMinutes,
        int cleanupMinutes)
    {
        await using var db = factory.Create(tenant);

        var t = new Tenant("Agenda Club", UniqueTaxId(), subdomain: $"ag-{Guid.NewGuid():N}"[..16]);
        var unit = new Unit(t.Id, "Matriz");
        var category = new AssetCategory { TenantId = t.Id, Name = "Equipamentos" };
        var family = new AssetFamily { Key = $"spaces-{Guid.NewGuid():N}"[..32], Label = "Spaces", FieldSchemaJson = "{}" };
        var asset = new Asset
        {
            TenantId = t.Id, UnitId = unit.Id, CategoryId = category.Id, FamilyId = family.Id,
            Name = "Impressora 3D", Tag = "P1", Status = AssetStatus.Active, IsRentable = true,
        };
        var rental = new RentalAsset
        {
            TenantId = t.Id, AssetId = asset.Id, Type = RentalAssetType.Location, TotalQuantity = 1,
            IsActive = true, RequiresDeposit = false, SchedulePolicy = SchedulePolicy.OpenHours,
            OpenTime = new TimeOnly(8, 0), CloseTime = new TimeOnly(22, 0), QueueEnabled = false,
        };
        var customer = new Customer { TenantId = t.Id, Name = "Ana", Email = $"{Guid.NewGuid():N}@club.test", Phone = "11999999999" };
        var product = new CatalogProduct { TenantId = t.Id, Name = "Impressao 3D", Price = 50m, Currency = "BRL", IsActive = true };

        db.Tenants.Add(t);
        db.Units.Add(unit);
        db.AssetCategories.Add(category);
        db.AssetFamilies.Add(family);
        db.Assets.Add(asset);
        db.RentalAssets.Add(rental);
        db.Customers.Add(customer);
        db.CatalogProducts.Add(product);
        db.TenantModules.Add(new TenantModule(t.Id, PlatformModules.Catalog, isActive: true));
        db.TenantModules.Add(new TenantModule(t.Id, PlatformModules.Rentals, isActive: true));
        db.TenantModules.Add(new TenantModule(t.Id, PlatformModules.ResourcePlanning, isActive: true));
        db.CatalogOrderNumberSequences.Add(new CatalogOrderNumberSequence { TenantId = t.Id, LastNumber = 1 });

        var order = new CatalogOrder { TenantId = t.Id, CustomerId = customer.Id, OrderNumber = 1, CustomerNameSnapshot = customer.Name, Currency = "BRL" };
        var item = new CatalogOrderItem
        {
            TenantId = t.Id, OrderId = order.Id, ProductId = product.Id, ProductNameSnapshot = product.Name,
            Quantity = 1, UnitPriceSnapshot = 50m, SubTotal = 50m, Currency = "BRL",
        };
        order.AddItem(item);
        db.CatalogOrders.Add(order);
        db.CatalogOrderItemResourceRequirements.Add(new CatalogOrderItemResourceRequirement
        {
            TenantId = t.Id, OrderItemId = item.Id, RentalAssetId = rental.Id,
            ResourceQuantity = 1, DurationMinutesPerUnit = 60, PrepMinutes = prepMinutes, CleanupMinutes = cleanupMinutes,
        });

        tenant.TenantId = t.Id;
        await db.SaveChangesAsync();

        return new Seed(t.Id, unit.Id, rental.Id, order.Id);
    }

    private static string UniqueTaxId() =>
        Random.Shared.NextInt64(10_000_000_000_000, 99_999_999_999_999).ToString();

    private sealed record Seed(Guid TenantId, Guid UnitId, Guid RentalAssetId, Guid OrderId);
}
