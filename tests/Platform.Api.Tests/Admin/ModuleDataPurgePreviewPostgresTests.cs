using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Modules.Admin.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Api.Tests.ResourcePlanning;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace Platform.Api.Tests.Admin;

/// <summary>
/// Real-PostgreSQL coverage of the module data purge preview (spec slice API-2 and amendment 7.1,
/// "FK reality check"). A gammera-like tenant (assets, rentals incl. rental assets and resource types,
/// catalog products/orders/allocations, planning allocations, os work orders, a pmoc plan) is seeded
/// next to an identical tenant, and the preview runs for each purgeable module with the OTHER tenant
/// as the ambient tenant, so only the explicit tenant parameter can produce the right numbers.
/// Seeding switches off FK triggers for the seeding session only; the preview never depends on row
/// validity, and the real constraints are cross-checked against pg_constraint below.
/// The observed numbers are written to the test output; the findings are documented in
/// docs/plans/active/2026-11-20-module-data-purge.md (7.6).
/// </summary>
public sealed class ModuleDataPurgePreviewPostgresTests : IClassFixture<PostgresContainerFixture>
{
    private static readonly string[] PurgeableModules =
    [
        PlatformModules.Rentals,
        PlatformModules.Catalog,
        PlatformModules.WorkOrders,
        PlatformModules.Pmoc,
        PlatformModules.ResourcePlanning,
    ];

    private readonly PostgresContainerFixture _postgres;
    private readonly ITestOutputHelper _output;

    public ModuleDataPurgePreviewPostgresTests(PostgresContainerFixture postgres, ITestOutputHelper output)
    {
        _postgres = postgres;
        _output = output;
    }

    [DockerFact]
    public async Task Fk_reality_check_each_purgeable_module_with_the_other_tenant_as_the_ambient_tenant()
    {
        var factory = RequireFactory();
        var target = await SeedAsync(factory, modulesActive: false);
        var other = await SeedAsync(factory, modulesActive: true);
        var previews = await PreviewAllAsync(factory, target.TenantId, ambientTenant: other.TenantId);

        // rentals: its own rows plus only the bridged planning row. The blockers are the catalog rows
        // that point at a rental asset or a reservation item. Nothing in the assets schema blocks it.
        var rentals = previews[PlatformModules.Rentals];
        Assert.False(rentals.ModuleActive);
        Assert.Equal(2, Rows(rentals, "rentals.reservations"));
        Assert.Equal(2, Rows(rentals, "rentals.reservation_items"));
        Assert.Equal(1, Rows(rentals, "rentals.rental_assets"));
        Assert.Equal(1, Rows(rentals, "rentals.resource_types"));
        Assert.Equal(1, Rows(rentals, "planning.asset_allocations"));
        Assert.Equal(0, Rows(rentals, "catalog.catalog_orders"));
        Assert.Equal(0, rentals.StorageObjects);
        Assert.All(rentals.Blockers, blocker => Assert.Equal(ModuleDataPurgeService.ForeignKeyRestrictCode, blocker.Code));
        Assert.DoesNotContain(rentals.Blockers, blocker => blocker.Table!.StartsWith("assets.", StringComparison.Ordinal));
        Assert.Equal(1, BlockerRows(rentals, "catalog.order_item_resource_allocations"));
        Assert.Equal(1, BlockerRows(rentals, "catalog.product_resource_requirements"));
        Assert.Equal(1, BlockerRows(rentals, "catalog.order_item_resource_requirements"));

        // catalog: its own rows plus only the catalog-sourced planning row; the planning row that
        // points at an order item is purged with it, so it is not a blocker.
        var catalog = previews[PlatformModules.Catalog];
        Assert.Equal(1, Rows(catalog, "catalog.catalog_products"));
        Assert.Equal(1, Rows(catalog, "catalog.catalog_product_files"));
        Assert.Equal(1, Rows(catalog, "catalog.catalog_orders"));
        Assert.Equal(1, Rows(catalog, "catalog.catalog_order_items"));
        Assert.Equal(1, Rows(catalog, "catalog.catalog_order_status_history"));
        Assert.Equal(1, Rows(catalog, "catalog.product_requests"));
        Assert.Equal(1, Rows(catalog, "catalog.product_request_files"));
        Assert.Equal(1, Rows(catalog, "catalog.product_resource_requirements"));
        Assert.Equal(1, Rows(catalog, "catalog.order_item_resource_requirements"));
        Assert.Equal(1, Rows(catalog, "catalog.order_item_resource_allocations"));
        Assert.Equal(1, Rows(catalog, "planning.asset_allocations"));
        Assert.Equal(0, Rows(catalog, "rentals.reservations"));
        Assert.Equal(2, catalog.StorageObjects);
        Assert.Empty(catalog.Blockers);

        // os: work orders plus only the work-order-sourced planning row.
        var os = previews[PlatformModules.WorkOrders];
        Assert.Equal(1, Rows(os, "os.work_orders"));
        Assert.Equal(1, Rows(os, "os.work_order_tasks"));
        Assert.Equal(1, Rows(os, "os.work_order_task_files"));
        Assert.Equal(1, Rows(os, "planning.asset_allocations"));
        Assert.Equal(1, os.StorageObjects);
        Assert.Empty(os.Blockers);

        // pmoc: the plan; the work order that points at it blocks the purge until os is purged.
        var pmoc = previews[PlatformModules.Pmoc];
        Assert.Equal(1, Rows(pmoc, "pmoc.maintenance_plans"));
        Assert.Equal(0, Rows(pmoc, "planning.asset_allocations"));
        Assert.Equal(0, pmoc.StorageObjects);
        var pmocBlocker = Assert.Single(pmoc.Blockers);
        Assert.Equal(ModuleDataPurgeService.ForeignKeyRestrictCode, pmocBlocker.Code);
        Assert.Equal("os.work_orders", pmocBlocker.Table);
        Assert.Equal(1, pmocBlocker.Rows);

        // resource-planning: the whole planning schema; catalog allocations point at its rows.
        var planning = previews[PlatformModules.ResourcePlanning];
        Assert.Equal(3, Rows(planning, "planning.asset_allocations"));
        Assert.Equal(2, Rows(planning, "planning.asset_planning_profiles"));
        Assert.Equal(0, planning.StorageObjects);
        var planningBlocker = Assert.Single(planning.Blockers);
        Assert.Equal(ModuleDataPurgeService.ForeignKeyRestrictCode, planningBlocker.Code);
        Assert.Equal("catalog.order_item_resource_allocations", planningBlocker.Table);
        Assert.Equal(1, planningBlocker.Rows);

        Dump(previews);
        await DumpForeignKeysIntoModuleSchemasAsync(factory);
    }

    [DockerFact]
    public async Task The_real_database_has_no_foreign_key_from_the_assets_schema_into_a_module_schema()
    {
        var factory = RequireFactory();

        var foreignKeys = await ForeignKeysIntoModuleSchemasAsync(factory);

        // Amendment 7.1: assets are the shared registry. If one of its tables ever points into a module
        // schema, the module purge needs an explicit step for it (never deleting the assets themselves).
        Assert.DoesNotContain(foreignKeys, line => line.StartsWith("assets.", StringComparison.Ordinal));
    }

    [DockerFact]
    public async Task Another_tenants_rows_are_never_counted()
    {
        var factory = RequireFactory();
        var empty = await SeedEmptyTenantAsync(factory);
        var other = await SeedAsync(factory, modulesActive: true);

        var previews = await PreviewAllAsync(factory, empty, ambientTenant: other.TenantId);

        foreach (var (module, preview) in previews)
        {
            Assert.Equal(module, preview.Module);
            Assert.False(preview.ModuleActive);
            Assert.Empty(preview.Tables);
            Assert.Equal(0, preview.StorageObjects);
            Assert.Empty(preview.Blockers);
        }
    }

    [DockerFact]
    public async Task A_target_with_the_same_shape_as_another_tenant_is_counted_once()
    {
        var factory = RequireFactory();
        var first = await SeedAsync(factory, modulesActive: false);
        var second = await SeedAsync(factory, modulesActive: false);

        var firstPreviews = await PreviewAllAsync(factory, first.TenantId, ambientTenant: null);
        var secondPreviews = await PreviewAllAsync(factory, second.TenantId, ambientTenant: first.TenantId);

        foreach (var module in PurgeableModules)
        {
            Assert.Equal(
                firstPreviews[module].Tables.Select(t => (t.Table, t.Rows)),
                secondPreviews[module].Tables.Select(t => (t.Table, t.Rows)));
            Assert.Equal(firstPreviews[module].StorageObjects, secondPreviews[module].StorageObjects);
        }
    }

    [DockerFact]
    public async Task An_active_module_is_a_blocker_until_it_is_disabled()
    {
        var factory = RequireFactory();
        var target = await SeedAsync(factory, modulesActive: true);

        var before = await PreviewAllAsync(factory, target.TenantId, ambientTenant: null);
        foreach (var module in PurgeableModules)
        {
            Assert.True(before[module].ModuleActive, module);
            var active = Assert.Single(before[module].Blockers, b => b.Code == ModuleDataPurgeService.ModuleActiveCode);
            Assert.Null(active.Table);
            Assert.Null(active.Rows);
        }

        await using (var db = factory.Create(new FakeTenantProvider()))
        {
            await db.TenantModules
                .Where(m => m.TenantId == target.TenantId && m.ModuleName == PlatformModules.Rentals)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsActive, false));
        }

        var after = await PreviewAllAsync(factory, target.TenantId, ambientTenant: null);
        Assert.False(after[PlatformModules.Rentals].ModuleActive);
        Assert.DoesNotContain(after[PlatformModules.Rentals].Blockers, b => b.Code == ModuleDataPurgeService.ModuleActiveCode);
        Assert.True(after[PlatformModules.Catalog].ModuleActive);
        Assert.Contains(after[PlatformModules.Catalog].Blockers, b => b.Code == ModuleDataPurgeService.ModuleActiveCode);
    }

    [DockerFact]
    public async Task Unreleased_allocations_block_only_the_module_that_owns_them()
    {
        var factory = RequireFactory();
        var target = await SeedAsync(factory, modulesActive: false);
        await using (var db = factory.Create(new FakeTenantProvider()))
        {
            // The catalog-sourced, rental-bridged planning row is running; the catalog allocation is
            // scheduled; the manual planning row is scheduled but never started and long expired.
            await db.AssetAllocations.Where(a => a.Id == target.CatalogPlanningId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AllocationStatus.Running));
            await db.CatalogOrderItemResourceAllocations.Where(a => a.Id == target.CatalogAllocationId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AllocationStatus.Scheduled));
            await db.AssetAllocations.Where(a => a.Id == target.ManualPlanningId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AllocationStatus.Scheduled));
        }

        var previews = await PreviewAllAsync(factory, target.TenantId, ambientTenant: null);

        Assert.Equal(1, UnreleasedRows(previews[PlatformModules.Catalog], "catalog.order_item_resource_allocations"));
        Assert.Equal(1, UnreleasedRows(previews[PlatformModules.Catalog], "planning.asset_allocations"));
        Assert.Equal(1, UnreleasedRows(previews[PlatformModules.Rentals], "planning.asset_allocations"));
        Assert.Equal(1, UnreleasedRows(previews[PlatformModules.ResourcePlanning], "planning.asset_allocations"));
        Assert.DoesNotContain(previews[PlatformModules.WorkOrders].Blockers, b => b.Code == ModuleDataPurgeService.UnreleasedAllocationsCode);
        Assert.DoesNotContain(previews[PlatformModules.Pmoc].Blockers, b => b.Code == ModuleDataPurgeService.UnreleasedAllocationsCode);
    }

    private PostgresAppDbFactory RequireFactory()
    {
        Assert.NotNull(_postgres.Factory);
        return _postgres.Factory;
    }

    private static long Rows(ModuleDataPurgePreviewDto preview, string table) =>
        preview.Tables.SingleOrDefault(t => t.Table == table)?.Rows ?? 0;

    private static long? BlockerRows(ModuleDataPurgePreviewDto preview, string table) =>
        preview.Blockers.SingleOrDefault(b => b.Code == ModuleDataPurgeService.ForeignKeyRestrictCode && b.Table == table)?.Rows;

    private static long? UnreleasedRows(ModuleDataPurgePreviewDto preview, string table) =>
        preview.Blockers.SingleOrDefault(b => b.Code == ModuleDataPurgeService.UnreleasedAllocationsCode && b.Table == table)?.Rows;

    private static async Task<Dictionary<string, ModuleDataPurgePreviewDto>> PreviewAllAsync(
        PostgresAppDbFactory factory,
        Guid tenantId,
        Guid? ambientTenant)
    {
        await using var db = factory.Create(new FakeTenantProvider { TenantId = ambientTenant });
        var service = new ModuleDataPurgeService(db, NullLogger<ModuleDataPurgeService>.Instance);
        var previews = new Dictionary<string, ModuleDataPurgePreviewDto>(StringComparer.Ordinal);
        foreach (var module in PurgeableModules)
            previews[module] = await service.PreviewAsync(tenantId, module, CancellationToken.None);
        return previews;
    }

    private void Dump(Dictionary<string, ModuleDataPurgePreviewDto> previews)
    {
        foreach (var (module, preview) in previews)
        {
            _output.WriteLine($"[{module}] active={preview.ModuleActive} storage={preview.StorageObjects}");
            foreach (var table in preview.Tables)
                _output.WriteLine($"  table {table.Table} rows={table.Rows}");
            foreach (var blocker in preview.Blockers)
                _output.WriteLine($"  blocker {blocker.Code} table={blocker.Table} rows={blocker.Rows}");
        }
    }

    private async Task DumpForeignKeysIntoModuleSchemasAsync(PostgresAppDbFactory factory)
    {
        _output.WriteLine("Foreign keys from another schema into a module schema (delete rule: a no action, r restrict, c cascade, n set null):");
        foreach (var line in await ForeignKeysIntoModuleSchemasAsync(factory))
            _output.WriteLine("  " + line);
    }

    private static async Task<List<string>> ForeignKeysIntoModuleSchemasAsync(PostgresAppDbFactory factory)
    {
        await using var db = factory.Create(new FakeTenantProvider());
        return await db.Database.SqlQueryRaw<string>(
                """
                SELECT cn.nspname || '.' || c.relname || ' -> ' || pn.nspname || '.' || p.relname
                    || ' [' || con.confdeltype::text || ']' AS "Value"
                FROM pg_constraint con
                JOIN pg_class c ON c.oid = con.conrelid
                JOIN pg_namespace cn ON cn.oid = c.relnamespace
                JOIN pg_class p ON p.oid = con.confrelid
                JOIN pg_namespace pn ON pn.oid = p.relnamespace
                WHERE con.contype = 'f'
                  AND cn.nspname <> pn.nspname
                  AND pn.nspname IN ('rentals', 'catalog', 'os', 'pmoc', 'planning')
                ORDER BY 1
                """)
            .ToListAsync();
    }

    private sealed record Dataset(
        Guid TenantId,
        Guid CatalogPlanningId,
        Guid ManualPlanningId,
        Guid WorkOrderPlanningId,
        Guid CatalogAllocationId);

    private static async Task<Guid> SeedEmptyTenantAsync(PostgresAppDbFactory factory)
    {
        await using var db = factory.Create(new FakeTenantProvider());
        var tenant = new Tenant(
            "Empty tenant",
            Random.Shared.NextInt64(10_000_000_000_000, 99_999_999_999_999).ToString(),
            subdomain: $"empty-{Guid.NewGuid():N}"[..16]);
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    /// <summary>
    /// One gammera-like tenant. Foreign key triggers are off for the seeding session only (needs the
    /// superuser the test container and the local test database use): the ids still line up.
    /// </summary>
    private static async Task<Dataset> SeedAsync(PostgresAppDbFactory factory, bool modulesActive)
    {
        await using var db = factory.Create(new FakeTenantProvider());
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET session_replication_role = replica");

        var seed = PlanningTestData.AddTenant(db, "Gammera like", withPlanningModule: false);
        var tenantId = seed.TenantId;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var windowStart = DateTimeOffset.UtcNow.AddDays(-10);
        var windowEnd = windowStart.AddHours(1);

        foreach (var module in new[]
                 {
                     PlatformModules.Inventory, PlatformModules.Rentals, PlatformModules.Catalog,
                     PlatformModules.WorkOrders, PlatformModules.Pmoc, PlatformModules.ResourcePlanning,
                 })
        {
            db.TenantModules.Add(new TenantModule(tenantId, module, modulesActive));
        }

        var customer = new Customer { TenantId = tenantId, Name = "Cliente", Email = $"c-{suffix}@club.test" };
        db.Customers.Add(customer);

        // rentals: resource type, rental asset, one customer booking, one internal-use reservation.
        var resourceType = new ResourceType
        {
            TenantId = tenantId,
            Key = "court",
            Name = "Quadras",
            Singular = "Quadra",
            Plural = "Quadras",
            Article = "uma",
        };
        var rentalAsset = new RentalAsset
        {
            TenantId = tenantId,
            AssetId = seed.AssetId,
            Type = RentalAssetType.Location,
            TotalQuantity = 1,
            IsActive = true,
            RequiresDeposit = false,
            SchedulePolicy = SchedulePolicy.OpenHours,
            ResourceTypeId = resourceType.Id,
        };
        var booking = new Reservation
        {
            TenantId = tenantId,
            UnitId = seed.UnitId,
            Purpose = ReservationPurpose.CustomerBooking,
            CustomerId = customer.Id,
            CustomerName = customer.Name,
            CustomerWhatsApp = "5511999990000",
            StartDateTime = windowStart.AddDays(1),
            EndDateTime = windowStart.AddDays(1).AddHours(1),
            Status = ReservationStatus.Confirmed,
            TotalAmount = 10m,
            DepositPaid = 0m,
        };
        var internalUse = new Reservation
        {
            TenantId = tenantId,
            UnitId = seed.UnitId,
            Purpose = ReservationPurpose.InternalUse,
            StartDateTime = windowStart,
            EndDateTime = windowEnd,
            Status = ReservationStatus.Confirmed,
            TotalAmount = 0m,
            DepositPaid = 0m,
        };
        var bookingItem = new ReservationItem
        {
            TenantId = tenantId,
            ReservationId = booking.Id,
            RentalAssetId = rentalAsset.Id,
            Quantity = 1,
            UnitPrice = 10m,
            SubTotal = 10m,
        };
        var internalItem = new ReservationItem
        {
            TenantId = tenantId,
            ReservationId = internalUse.Id,
            RentalAssetId = rentalAsset.Id,
            Quantity = 1,
            UnitPrice = 0m,
            SubTotal = 0m,
        };
        db.ResourceTypes.Add(resourceType);
        db.RentalAssets.Add(rentalAsset);
        db.Reservations.AddRange(booking, internalUse);
        db.ReservationItems.AddRange(bookingItem, internalItem);

        // catalog: product with a file, order with an item, a free-text request with a file.
        var product = new CatalogProduct
        {
            TenantId = tenantId,
            Name = "Item",
            Code = "ITEM",
            Price = 10m,
            Currency = "BRL",
            IsActive = true,
        };
        product.AddFile(new CatalogProductFile
        {
            TenantId = tenantId,
            ProductId = product.Id,
            StorageKey = $"{tenantId:N}/{product.Id}/photo.png",
            FileName = "photo.png",
            MimeType = "image/png",
            SizeBytes = 4,
            Visibility = CatalogFileVisibility.CustomerVisible,
        });
        var order = new CatalogOrder
        {
            TenantId = tenantId,
            CustomerId = customer.Id,
            OrderNumber = 1,
            CustomerNameSnapshot = customer.Name,
            CustomerEmailSnapshot = customer.Email,
            Currency = "BRL",
        };
        var orderItem = new CatalogOrderItem
        {
            TenantId = tenantId,
            OrderId = order.Id,
            ProductId = product.Id,
            ProductNameSnapshot = product.Name,
            Quantity = 1,
            UnitPriceSnapshot = 10m,
            SubTotal = 10m,
            Currency = "BRL",
        };
        order.AddItem(orderItem);
        order.RecordHistory(CatalogOrderStatus.Requested, CatalogActorType.Customer, customer.Id, reason: null);
        var request = new ProductRequest
        {
            TenantId = tenantId,
            CustomerId = customer.Id,
            Description = "Item fora do catalogo",
            Quantity = 1,
            Status = ProductRequestStatus.Submitted,
        };
        request.AddFile(new ProductRequestFile
        {
            TenantId = tenantId,
            ProductRequestId = request.Id,
            StorageKey = $"{tenantId:N}/requests/{request.Id}/note.txt",
            FileName = "note.txt",
            MimeType = "text/plain",
            SizeBytes = 4,
            Visibility = CatalogFileVisibility.InternalB2B,
        });
        var productRequirement = new CatalogProductResourceRequirement
        {
            TenantId = tenantId,
            ProductId = product.Id,
            AssetId = seed.AssetId,
            RentalAssetId = rentalAsset.Id,
            ResourceQuantity = 1,
            DurationMinutesPerUnit = 60,
            Version = 1,
        };
        var orderRequirement = new CatalogOrderItemResourceRequirement
        {
            TenantId = tenantId,
            OrderItemId = orderItem.Id,
            SourceRequirementId = productRequirement.Id,
            AssetId = seed.AssetId,
            RentalAssetId = rentalAsset.Id,
            ResourceQuantity = 1,
            DurationMinutesPerUnit = 60,
        };
        db.CatalogProducts.Add(product);
        db.CatalogOrders.Add(order);
        db.ProductRequests.Add(request);
        db.CatalogProductResourceRequirements.Add(productRequirement);
        db.CatalogOrderItemResourceRequirements.Add(orderRequirement);

        // pmoc plan and the os work order generated from it, with one task image.
        var plan = new MaintenancePlan
        {
            TenantId = tenantId,
            UnitId = seed.UnitId,
            Name = "Plano mensal",
            IntervalDays = 30,
            FirstDueDate = DateOnly.FromDateTime(DateTime.UtcNow),
            AssetId = seed.SecondAssetId,
            IsActive = true,
        };
        var workOrder = new WorkOrder
        {
            TenantId = tenantId,
            AssetId = seed.SecondAssetId,
            Number = 1,
            MaintenancePlanId = plan.Id,
            Status = WorkOrderStatus.Completed,
            ScheduledDate = DateOnly.FromDateTime(DateTime.UtcNow),
        };
        var task = new WorkOrderTask
        {
            TenantId = tenantId,
            WorkOrderId = workOrder.Id,
            Title = "Foto",
            InputType = TaskInputType.Image,
            IsMandatory = false,
            Order = 1,
        };
        var taskFile = new WorkOrderTaskFile
        {
            TenantId = tenantId,
            WorkOrderId = workOrder.Id,
            WorkOrderTaskId = task.Id,
            StorageKey = $"{tenantId:N}/work-orders/{workOrder.Id}/{task.Id}.jpg",
            FileName = "task.jpg",
            ContentType = "image/jpeg",
            SizeBytes = 4,
        };
        db.MaintenancePlans.Add(plan);
        db.WorkOrders.Add(workOrder);
        db.WorkOrderTasks.Add(task);
        db.WorkOrderTaskFiles.Add(taskFile);

        // planning: one row per source (catalog order bridged to a reservation item, work order,
        // manual) and the catalog allocation that points at the first one. All released.
        var catalogPlanning = Planning(
            tenantId, seed, windowStart, windowEnd, AssetAllocationSource.CatalogOrder, seed.AssetId);
        catalogPlanning.CatalogOrderItemId = orderItem.Id;
        catalogPlanning.ReservationItemId = internalItem.Id;
        catalogPlanning.ReferenceLabel = "Pedido 1";
        var workOrderPlanning = Planning(
            tenantId, seed, windowStart, windowEnd, AssetAllocationSource.WorkOrder, seed.SecondAssetId);
        workOrderPlanning.WorkOrderId = workOrder.Id;
        var manualPlanning = Planning(
            tenantId, seed, windowStart, windowEnd, AssetAllocationSource.Manual, seed.SecondAssetId);
        var catalogAllocation = new CatalogOrderItemResourceAllocation
        {
            TenantId = tenantId,
            RequirementId = orderRequirement.Id,
            AssetAllocationId = catalogPlanning.Id,
            ReservationItemId = internalItem.Id,
            Sequence = 1,
            PlanningRevision = 1,
            AllocatedQuantity = 1,
            RentalAssetId = rentalAsset.Id,
            PlannedWorkStart = windowStart,
            PlannedWorkEnd = windowEnd,
            TimeZoneId = "America/Sao_Paulo",
            Status = AllocationStatus.Released,
        };
        db.AssetAllocations.AddRange(catalogPlanning, workOrderPlanning, manualPlanning);
        db.CatalogOrderItemResourceAllocations.Add(catalogAllocation);

        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("SET session_replication_role = DEFAULT");

        return new Dataset(
            tenantId,
            catalogPlanning.Id,
            manualPlanning.Id,
            workOrderPlanning.Id,
            catalogAllocation.Id);
    }

    private static AssetAllocation Planning(
        Guid tenantId,
        PlanningTenantSeed seed,
        DateTimeOffset start,
        DateTimeOffset end,
        AssetAllocationSource source,
        Guid assetId) =>
        new()
        {
            TenantId = tenantId,
            AssetId = assetId,
            UnitId = seed.UnitId,
            Title = $"Planning {source}",
            PlannedWorkStart = start,
            PlannedWorkEnd = end,
            OccupiedStart = start,
            OccupiedEnd = end,
            TimeZoneId = "America/Sao_Paulo",
            Status = AllocationStatus.Released,
            Source = source,
        };
}
