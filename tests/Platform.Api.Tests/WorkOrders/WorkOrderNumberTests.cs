using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Platform.Api.Modules.WorkOrders.Services;
using Platform.Api.Tests.Assets;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;

namespace Platform.Api.Tests.WorkOrders;

public sealed class WorkOrderNumberTests : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture _postgres;

    public WorkOrderNumberTests(PostgresContainerFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task Allocator_starts_at_one_and_counts_each_tenant_independently()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var tenantA = harness.TenantProvider.TenantId!.Value;
        var tenantB = Guid.NewGuid();

        var a1 = await WorkOrderNumberAllocator.AllocateAsync(harness.Db, tenantA, CancellationToken.None);
        await harness.Db.SaveChangesAsync();
        var a2 = await WorkOrderNumberAllocator.AllocateAsync(harness.Db, tenantA, CancellationToken.None);
        await harness.Db.SaveChangesAsync();

        harness.TenantProvider.TenantId = tenantB;
        var b1 = await WorkOrderNumberAllocator.AllocateAsync(harness.Db, tenantB, CancellationToken.None);
        await harness.Db.SaveChangesAsync();

        Assert.Equal(1, a1);
        Assert.Equal(2, a2);
        Assert.Equal(1, b1);
    }

    [DockerFact]
    public async Task Allocator_on_postgres_counts_per_tenant_and_never_repeats_under_concurrency()
    {
        var factory = Assert.IsType<PostgresAppDbFactory>(_postgres.Factory);
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        async Task<int> AllocateInTransactionAsync(Guid tenantId)
        {
            var provider = new FakeTenantProvider { TenantId = tenantId };
            await using var db = factory.Create(provider);
            await using var transaction = await db.Database.BeginTransactionAsync();
            var number = await WorkOrderNumberAllocator.AllocateAsync(db, tenantId, CancellationToken.None);
            await Task.Delay(20);
            await transaction.CommitAsync();
            return number;
        }

        var concurrent = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ => AllocateInTransactionAsync(tenantA)));
        var other = await AllocateInTransactionAsync(tenantB);
        var next = await AllocateInTransactionAsync(tenantA);

        Assert.Equal(Enumerable.Range(1, 10), concurrent.OrderBy(n => n));
        Assert.Equal(1, other);
        Assert.Equal(11, next);
    }

    [DockerFact]
    public async Task Migration_backfills_existing_work_orders_per_tenant_and_seeds_the_counter()
    {
        var factory = Assert.IsType<PostgresAppDbFactory>(_postgres.Factory);
        var provider = new FakeTenantProvider();
        await using var db = factory.Create(provider);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261115120000_AddCatalogProductUsesSchedule");

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var a1 = Guid.NewGuid();
        var a2 = Guid.NewGuid();
        var a3 = Guid.NewGuid();
        var b1 = Guid.NewGuid();
        var t0 = DateTimeOffset.Parse("2026-01-01T10:00:00Z");

        // Raw SQL: the current EF model has columns that do not exist at this older migration.
        // Foreign keys are skipped for the legacy inserts (assets/tenants are irrelevant here).
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET session_replication_role = replica");
        await InsertLegacyWorkOrderAsync(db, a2, tenantA, t0.AddMinutes(5));
        await InsertLegacyWorkOrderAsync(db, a1, tenantA, t0);
        await InsertLegacyWorkOrderAsync(db, a3, tenantA, t0.AddMinutes(10));
        await InsertLegacyWorkOrderAsync(db, b1, tenantB, t0.AddMinutes(1));
        await db.Database.ExecuteSqlRawAsync("SET session_replication_role = DEFAULT");

        await migrator.MigrateAsync();

        var numbers = new Dictionary<Guid, int>();
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "SELECT id, number FROM os.work_orders";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                numbers[reader.GetGuid(0)] = reader.GetInt32(1);
            }
        }

        Assert.Equal(1, numbers[a1]);
        Assert.Equal(2, numbers[a2]);
        Assert.Equal(3, numbers[a3]);
        Assert.Equal(1, numbers[b1]);

        var seeded = new Dictionary<Guid, int>();
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "SELECT tenant_id, last_number FROM os.work_order_number_sequences";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                seeded[reader.GetGuid(0)] = reader.GetInt32(1);
            }
        }

        Assert.Equal(3, seeded[tenantA]);
        Assert.Equal(1, seeded[tenantB]);

        // New allocations continue after the backfilled max.
        var providerA = new FakeTenantProvider { TenantId = tenantA };
        await using var after = factory.Create(providerA);
        await using var transaction = await after.Database.BeginTransactionAsync();
        Assert.Equal(4, await WorkOrderNumberAllocator.AllocateAsync(after, tenantA, CancellationToken.None));
        await transaction.RollbackAsync();
    }

    private static Task<int> InsertLegacyWorkOrderAsync(
        Platform.Core.Infrastructure.Persistence.AppDbContext db,
        Guid id,
        Guid tenantId,
        DateTimeOffset createdAt) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO os.work_orders (id, tenant_id, asset_id, status, scheduled_date, created_at)
            VALUES ({id}, {tenantId}, {Guid.NewGuid()}, 'Pending', DATE '2026-02-01', {createdAt})
            """);
}
