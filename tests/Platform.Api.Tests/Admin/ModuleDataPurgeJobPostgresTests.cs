using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Jobs;
using Platform.Api.Modules.Admin.Services;
using Platform.Api.Storage;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Admin;

/// <summary>
/// Real-PostgreSQL coverage of the daily <see cref="ModuleDataPurgeJob"/> (spec slice API-3, amendments
/// 7.1 to 7.3). Two identical gammera-like tenants are seeded (see
/// <see cref="ModuleDataPurgePreviewPostgresTests"/>); the job runs for one of them and the other must
/// be untouched, row for row.
/// </summary>
public sealed class ModuleDataPurgeJobPostgresTests : IClassFixture<PostgresContainerFixture>
{
    private static readonly string[] Tables =
    [
        "catalog.catalog_products",
        "catalog.catalog_product_files",
        "catalog.catalog_orders",
        "catalog.catalog_order_items",
        "catalog.product_requests",
        "catalog.order_item_resource_allocations",
        "planning.asset_allocations",
        "rentals.reservations",
        "rentals.reservation_items",
        "rentals.rental_assets",
        "os.work_orders",
        "os.work_order_tasks",
        "os.work_order_task_files",
        "pmoc.maintenance_plans",
    ];

    private readonly PostgresContainerFixture _postgres;

    public ModuleDataPurgeJobPostgresTests(PostgresContainerFixture postgres)
    {
        _postgres = postgres;
    }

    [DockerFact]
    public async Task A_due_catalog_purge_deletes_only_that_tenants_catalog_rows_and_its_storage_objects()
    {
        var factory = RequireFactory();
        var target = await ModuleDataPurgePreviewPostgresTests.SeedAsync(factory, modulesActive: false);
        var other = await ModuleDataPurgePreviewPostgresTests.SeedAsync(factory, modulesActive: false);
        var otherBefore = await SnapshotAsync(factory, other.TenantId);
        var targetBefore = await SnapshotAsync(factory, target.TenantId);
        Assert.Equal(otherBefore, targetBefore);
        var purgeId = await InsertPurgeAsync(factory, target.TenantId, PlatformModules.Catalog, due: true);

        // The storage delete must run after the commit: by then the rows are already gone.
        var rowsWhenStorageWasTouched = new List<long>();
        var storage = new RecordingStorage
        {
            OnDelete = async () => rowsWhenStorageWasTouched.Add(
                await CountAsync(factory, "catalog.catalog_orders", target.TenantId)),
        };

        await RunJobAsync(factory, storage);

        var after = await SnapshotAsync(factory, target.TenantId);
        Assert.Equal(0, after["catalog.catalog_products"]);
        Assert.Equal(0, after["catalog.catalog_product_files"]);
        Assert.Equal(0, after["catalog.catalog_orders"]);
        Assert.Equal(0, after["catalog.catalog_order_items"]);
        Assert.Equal(0, after["catalog.product_requests"]);
        Assert.Equal(0, after["catalog.order_item_resource_allocations"]);
        // Planning rows are borrowed: only the catalog-sourced one goes, the work-order and manual rows stay.
        Assert.Equal(2, after["planning.asset_allocations"]);
        Assert.Equal(1, await CountAsync(factory, "planning.asset_allocations", target.TenantId, "source = 'WorkOrder'"));
        Assert.Equal(1, await CountAsync(factory, "planning.asset_allocations", target.TenantId, "source = 'Manual'"));
        // Every other module of the same tenant is intact.
        Assert.Equal(targetBefore["rentals.reservations"], after["rentals.reservations"]);
        Assert.Equal(targetBefore["rentals.reservation_items"], after["rentals.reservation_items"]);
        Assert.Equal(targetBefore["rentals.rental_assets"], after["rentals.rental_assets"]);
        Assert.Equal(targetBefore["os.work_orders"], after["os.work_orders"]);
        Assert.Equal(targetBefore["os.work_order_task_files"], after["os.work_order_task_files"]);
        Assert.Equal(targetBefore["pmoc.maintenance_plans"], after["pmoc.maintenance_plans"]);

        // The other tenant has identical rows and none of them moved.
        Assert.Equal(otherBefore, await SnapshotAsync(factory, other.TenantId));

        // Storage keys: both catalog files of the target, in the private bucket, never the other tenant's.
        Assert.Equal(2, storage.Deletes.Count);
        Assert.All(storage.Deletes, d =>
        {
            Assert.Equal(new StorageOptions().PrivateBucket, d.Bucket);
            Assert.StartsWith($"{target.TenantId:N}/", d.Key, StringComparison.Ordinal);
        });
        Assert.NotEmpty(rowsWhenStorageWasTouched);
        Assert.All(rowsWhenStorageWasTouched, rows => Assert.Equal(0, rows));

        var purge = await GetPurgeAsync(factory, purgeId);
        Assert.NotNull(purge.ExecutedAt);
        Assert.Null(purge.CancelledAt);
        using var result = JsonDocument.Parse(purge.ResultJson!);
        var tables = result.RootElement.GetProperty("tables");
        Assert.Equal(1, tables.GetProperty("catalog.catalog_orders").GetInt64());
        Assert.Equal(1, tables.GetProperty("planning.asset_allocations").GetInt64());
        Assert.False(tables.TryGetProperty("rentals.reservations", out _));
        Assert.Equal(2, result.RootElement.GetProperty("storageObjects").GetInt32());
        Assert.Equal(2, result.RootElement.GetProperty("storageDeleted").GetInt32());
        Assert.Equal(0, result.RootElement.GetProperty("storageFailed").GetInt32());

        // Nothing is left to do for the next run.
        var second = new RecordingStorage();
        await RunJobAsync(factory, second);
        Assert.Empty(second.Deletes);
    }

    [DockerFact]
    public async Task A_due_os_purge_takes_only_the_work_order_planning_rows()
    {
        var factory = RequireFactory();
        var target = await ModuleDataPurgePreviewPostgresTests.SeedAsync(factory, modulesActive: false);
        var other = await ModuleDataPurgePreviewPostgresTests.SeedAsync(factory, modulesActive: false);
        var otherBefore = await SnapshotAsync(factory, other.TenantId);
        var targetBefore = await SnapshotAsync(factory, target.TenantId);
        await InsertPurgeAsync(factory, target.TenantId, PlatformModules.WorkOrders, due: true);
        var storage = new RecordingStorage();

        await RunJobAsync(factory, storage);

        var after = await SnapshotAsync(factory, target.TenantId);
        Assert.Equal(0, after["os.work_orders"]);
        Assert.Equal(0, after["os.work_order_tasks"]);
        Assert.Equal(0, after["os.work_order_task_files"]);
        Assert.Equal(2, after["planning.asset_allocations"]);
        Assert.Equal(0, await CountAsync(factory, "planning.asset_allocations", target.TenantId, "source = 'WorkOrder'"));
        Assert.Equal(1, await CountAsync(factory, "planning.asset_allocations", target.TenantId, "source = 'CatalogOrder'"));
        Assert.Equal(targetBefore["catalog.catalog_orders"], after["catalog.catalog_orders"]);
        Assert.Equal(targetBefore["rentals.reservations"], after["rentals.reservations"]);
        Assert.Equal(targetBefore["pmoc.maintenance_plans"], after["pmoc.maintenance_plans"]);
        Assert.Equal(otherBefore, await SnapshotAsync(factory, other.TenantId));

        var key = Assert.Single(storage.Deletes);
        Assert.StartsWith($"{target.TenantId:N}/work-orders/", key.Key, StringComparison.Ordinal);
    }

    [DockerFact]
    public async Task A_purge_that_is_not_yet_due_is_left_alone()
    {
        var factory = RequireFactory();
        var target = await ModuleDataPurgePreviewPostgresTests.SeedAsync(factory, modulesActive: false);
        var before = await SnapshotAsync(factory, target.TenantId);
        var purgeId = await InsertPurgeAsync(factory, target.TenantId, PlatformModules.Catalog, due: false);
        var storage = new RecordingStorage();

        await RunJobAsync(factory, storage);

        Assert.Equal(before, await SnapshotAsync(factory, target.TenantId));
        Assert.Empty(storage.Deletes);
        var purge = await GetPurgeAsync(factory, purgeId);
        Assert.Null(purge.ExecutedAt);
        Assert.Null(purge.CancelledAt);
    }

    [DockerFact]
    public async Task A_cancelled_purge_is_never_executed()
    {
        var factory = RequireFactory();
        var target = await ModuleDataPurgePreviewPostgresTests.SeedAsync(factory, modulesActive: false);
        var before = await SnapshotAsync(factory, target.TenantId);
        var purgeId = await InsertPurgeAsync(factory, target.TenantId, PlatformModules.Catalog, due: true);
        await using (var db = factory.Create(new FakeTenantProvider()))
        {
            var purge = await db.TenantModuleDataPurges.AsTracking().SingleAsync(p => p.Id == purgeId);
            purge.Cancel(DateTimeOffset.UtcNow, "auth-sub", "changed my mind");
            await db.SaveChangesAsync();
        }

        await RunJobAsync(factory, new RecordingStorage());

        Assert.Equal(before, await SnapshotAsync(factory, target.TenantId));
        Assert.Null((await GetPurgeAsync(factory, purgeId)).ExecutedAt);
    }

    [DockerFact]
    public async Task A_module_that_is_active_again_cancels_the_purge_and_deletes_nothing()
    {
        var factory = RequireFactory();
        var target = await ModuleDataPurgePreviewPostgresTests.SeedAsync(factory, modulesActive: false);
        var before = await SnapshotAsync(factory, target.TenantId);
        var purgeId = await InsertPurgeAsync(factory, target.TenantId, PlatformModules.Catalog, due: true);
        await using (var db = factory.Create(new FakeTenantProvider()))
        {
            await db.TenantModules
                .Where(m => m.TenantId == target.TenantId && m.ModuleName == PlatformModules.Catalog)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsActive, true));
        }

        var storage = new RecordingStorage();
        await RunJobAsync(factory, storage);

        Assert.Equal(before, await SnapshotAsync(factory, target.TenantId));
        Assert.Empty(storage.Deletes);
        var purge = await GetPurgeAsync(factory, purgeId);
        Assert.NotNull(purge.CancelledAt);
        Assert.Null(purge.ExecutedAt);
        Assert.Null(purge.CancelledByUserId);
        Assert.StartsWith("blocked: module.active", purge.CancelReason, StringComparison.Ordinal);
    }

    [DockerFact]
    public async Task A_blocker_that_appears_after_scheduling_cancels_the_purge_and_deletes_nothing()
    {
        var factory = RequireFactory();
        var target = await ModuleDataPurgePreviewPostgresTests.SeedAsync(factory, modulesActive: false);
        var purgeId = await InsertPurgeAsync(factory, target.TenantId, PlatformModules.Catalog, due: true);
        await using (var db = factory.Create(new FakeTenantProvider()))
        {
            // The catalog allocation is running again: the same rule that blocks disabling a module.
            await db.CatalogOrderItemResourceAllocations
                .Where(a => a.Id == target.CatalogAllocationId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AllocationStatus.Running));
        }

        var before = await SnapshotAsync(factory, target.TenantId);
        var storage = new RecordingStorage();
        await RunJobAsync(factory, storage);

        Assert.Equal(before, await SnapshotAsync(factory, target.TenantId));
        Assert.Empty(storage.Deletes);
        var purge = await GetPurgeAsync(factory, purgeId);
        Assert.NotNull(purge.CancelledAt);
        Assert.Null(purge.ExecutedAt);
        Assert.Contains("allocations.unreleased", purge.CancelReason, StringComparison.Ordinal);
    }

    [DockerFact]
    public async Task A_foreign_key_blocker_cancels_the_purge_until_the_dependent_module_is_purged()
    {
        var factory = RequireFactory();
        var target = await ModuleDataPurgePreviewPostgresTests.SeedAsync(factory, modulesActive: false);
        var purgeId = await InsertPurgeAsync(factory, target.TenantId, PlatformModules.Pmoc, due: true);

        // The seed has a work order pointing at the maintenance plan: pmoc is blocked until os goes.
        var before = await SnapshotAsync(factory, target.TenantId);
        await RunJobAsync(factory, new RecordingStorage());

        Assert.Equal(before, await SnapshotAsync(factory, target.TenantId));
        var purge = await GetPurgeAsync(factory, purgeId);
        Assert.NotNull(purge.CancelledAt);
        Assert.Contains("fk.restrict os.work_orders", purge.CancelReason, StringComparison.Ordinal);
    }

    [DockerFact]
    public async Task A_purge_of_a_tenant_that_no_longer_exists_is_cancelled()
    {
        var factory = RequireFactory();
        var tenantId = await ModuleDataPurgePreviewPostgresTests.SeedEmptyTenantAsync(factory);
        var purgeId = await InsertPurgeAsync(factory, tenantId, PlatformModules.Catalog, due: true);
        await using (var db = factory.Create(new FakeTenantProvider()))
        {
            // Simulates the tenant disappearing between the job's scan and its lock: the FK cascade is
            // skipped on purpose, so the purge row survives its tenant.
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("SET session_replication_role = replica");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM core.tenants WHERE id = {tenantId}");
            await db.Database.ExecuteSqlRawAsync("SET session_replication_role = DEFAULT");
        }

        await RunJobAsync(factory, new RecordingStorage());

        var purge = await GetPurgeAsync(factory, purgeId);
        Assert.NotNull(purge.CancelledAt);
        Assert.Equal(ModuleDataPurgeJob.TenantDeletedReason, purge.CancelReason);
        Assert.Null(purge.ExecutedAt);
    }

    [DockerFact]
    public async Task A_failing_storage_delete_does_not_undo_the_purge_and_is_counted()
    {
        var factory = RequireFactory();
        var target = await ModuleDataPurgePreviewPostgresTests.SeedAsync(factory, modulesActive: false);
        var purgeId = await InsertPurgeAsync(factory, target.TenantId, PlatformModules.Catalog, due: true);
        var storage = new RecordingStorage { Fail = true };

        await RunJobAsync(factory, storage);

        Assert.Equal(2, storage.Deletes.Count);
        Assert.Equal(0, await CountAsync(factory, "catalog.catalog_orders", target.TenantId));
        var purge = await GetPurgeAsync(factory, purgeId);
        Assert.NotNull(purge.ExecutedAt);
        using var result = JsonDocument.Parse(purge.ResultJson!);
        Assert.Equal(0, result.RootElement.GetProperty("storageDeleted").GetInt32());
        Assert.Equal(2, result.RootElement.GetProperty("storageFailed").GetInt32());
    }

    [DockerFact]
    public async Task Rows_are_deleted_in_batches_until_none_are_left()
    {
        var factory = RequireFactory();
        var target = await ModuleDataPurgePreviewPostgresTests.SeedAsync(factory, modulesActive: false);
        var other = await ModuleDataPurgePreviewPostgresTests.SeedAsync(factory, modulesActive: false);
        await using (var db = factory.Create(new FakeTenantProvider()))
        {
            for (var index = 0; index < 7; index++)
            {
                db.CatalogProducts.Add(new CatalogProduct
                {
                    TenantId = target.TenantId,
                    Name = $"Extra {index}",
                    Code = $"EXTRA-{index}",
                    Price = 1m,
                    Currency = "BRL",
                    IsActive = true,
                });
            }

            await db.SaveChangesAsync();
        }

        var otherBefore = await SnapshotAsync(factory, other.TenantId);
        Assert.Equal(8, await CountAsync(factory, "catalog.catalog_products", target.TenantId));
        var purgeId = await InsertPurgeAsync(factory, target.TenantId, PlatformModules.Catalog, due: true);

        await RunJobAsync(factory, new RecordingStorage(), batchSize: 2);

        Assert.Equal(0, await CountAsync(factory, "catalog.catalog_products", target.TenantId));
        Assert.Equal(otherBefore, await SnapshotAsync(factory, other.TenantId));
        using var result = JsonDocument.Parse((await GetPurgeAsync(factory, purgeId)).ResultJson!);
        Assert.Equal(8, result.RootElement.GetProperty("tables").GetProperty("catalog.catalog_products").GetInt64());
    }

    [DockerFact]
    public async Task A_command_timeout_cancels_the_purge_and_commits_nothing()
    {
        var factory = RequireFactory();
        var target = await ModuleDataPurgePreviewPostgresTests.SeedAsync(factory, modulesActive: false);
        var purgeId = await InsertPurgeAsync(factory, target.TenantId, PlatformModules.Catalog, due: true);
        var before = await SnapshotAsync(factory, target.TenantId);

        // Another session holds a lock on one of the order rows, so the delete of that table waits.
        // The tables deleted before it (allocations, planning rows) must be rolled back with it.
        await using var blocker = factory.Create(new FakeTenantProvider());
        await using var blockerTransaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM catalog.catalog_orders WHERE tenant_id = {target.TenantId} FOR UPDATE");

        var storage = new RecordingStorage();
        await RunJobAsync(factory, storage, commandTimeout: TimeSpan.FromSeconds(1));
        await blockerTransaction.RollbackAsync();

        Assert.Equal(before, await SnapshotAsync(factory, target.TenantId));
        Assert.Empty(storage.Deletes);
        var purge = await GetPurgeAsync(factory, purgeId);
        Assert.NotNull(purge.CancelledAt);
        Assert.Null(purge.ExecutedAt);
        Assert.Equal(ModuleDataPurgeJob.TimeoutReason, purge.CancelReason);
    }

    private PostgresAppDbFactory RequireFactory()
    {
        Assert.NotNull(_postgres.Factory);
        return _postgres.Factory;
    }

    private static async Task RunJobAsync(
        PostgresAppDbFactory factory,
        IStorageProvider storage,
        int batchSize = 5000,
        TimeSpan? commandTimeout = null)
    {
        await using var db = factory.Create(new FakeTenantProvider());
        var job = new ModuleDataPurgeJob(
            db,
            new ModuleDataPurgeService(db, NullLogger<ModuleDataPurgeService>.Instance),
            storage,
            Options.Create(new StorageOptions()),
            NullLogger<ModuleDataPurgeJob>.Instance)
        {
            BatchSize = batchSize,
            CommandTimeout = commandTimeout ?? TimeSpan.FromMinutes(5),
        };
        await job.ExecuteAsync(CancellationToken.None);
    }

    private static async Task<Guid> InsertPurgeAsync(
        PostgresAppDbFactory factory,
        Guid tenantId,
        string module,
        bool due)
    {
        await using var db = factory.Create(new FakeTenantProvider());
        var scheduledFor = due ? DateTimeOffset.UtcNow.AddMinutes(-5) : DateTimeOffset.UtcNow.AddDays(29);
        var purge = new TenantModuleDataPurge
        {
            TenantId = tenantId,
            Module = module,
            RequestedByUserId = "auth-sub",
            RequestedAt = scheduledFor.AddDays(-30),
            ScheduledFor = scheduledFor,
        };
        db.TenantModuleDataPurges.Add(purge);
        await db.SaveChangesAsync();
        return purge.Id;
    }

    private static async Task<TenantModuleDataPurge> GetPurgeAsync(PostgresAppDbFactory factory, Guid purgeId)
    {
        await using var db = factory.Create(new FakeTenantProvider());
        return await db.TenantModuleDataPurges.AsNoTracking().SingleAsync(p => p.Id == purgeId);
    }

    private static async Task<Dictionary<string, long>> SnapshotAsync(PostgresAppDbFactory factory, Guid tenantId)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in Tables)
            counts[table] = await CountAsync(factory, table, tenantId);
        return counts;
    }

    private static async Task<long> CountAsync(
        PostgresAppDbFactory factory,
        string table,
        Guid tenantId,
        string? extraPredicate = null)
    {
        await using var db = factory.Create(new FakeTenantProvider());
        var predicate = extraPredicate is null ? string.Empty : $" AND {extraPredicate}";
        // The table and predicate are constants of this test; the tenant id is a parameter.
#pragma warning disable EF1002
        return await db.Database
            .SqlQueryRaw<long>($"SELECT COUNT(*) AS \"Value\" FROM {table} WHERE tenant_id = {{0}}{predicate}", tenantId)
            .SingleAsync();
#pragma warning restore EF1002
    }

    private sealed class RecordingStorage : IStorageProvider
    {
        public List<(string Bucket, string Key)> Deletes { get; } = [];

        public bool Fail { get; init; }

        public Func<Task>? OnDelete { get; init; }

        public async Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken = default)
        {
            Deletes.Add((bucket, key));
            if (OnDelete is not null)
                await OnDelete();
            if (Fail)
                throw new InvalidOperationException("Storage unavailable.");
        }

        public Task UploadAsync(string bucket, string key, Stream stream, string contentType, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public string GetPublicUrl(string bucket, string key) => throw new NotSupportedException();

        public Task<string> CreateSignedUrlAsync(string bucket, string key, TimeSpan ttl, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
