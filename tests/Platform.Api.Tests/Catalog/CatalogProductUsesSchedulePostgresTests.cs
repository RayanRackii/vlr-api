using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;

namespace Platform.Api.Tests.Catalog;

// InMemory ignores database defaults, so the real insert path needs Postgres.
public sealed class CatalogProductUsesSchedulePostgresTests(PostgresContainerFixture postgres)
    : IClassFixture<PostgresContainerFixture>
{
    [DockerFact]
    public async Task False_is_persisted_on_insert_and_existing_rows_default_to_true()
    {
        var factory = Assert.IsType<PostgresAppDbFactory>(postgres.Factory);
        var provider = new FakeTenantProvider();
        await using var db = factory.Create(provider);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261112183916_AddMaintenancePlanAssetTarget");
        var tenant = new Tenant("Uses schedule test", Guid.NewGuid().ToString("N")[..20]);
        provider.TenantId = tenant.Id;
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        // Raw SQL: the current EF model has columns that do not exist at this older migration.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO catalog.catalog_products (id, tenant_id, name, created_at)
            VALUES ({Guid.NewGuid()}, {tenant.Id}, 'Legacy', now())
            """);

        await migrator.MigrateAsync();

        var off = new CatalogProduct { TenantId = tenant.Id, Name = "Off", UsesSchedule = false, IsActive = false };
        var on = new CatalogProduct { TenantId = tenant.Id, Name = "On" };
        db.CatalogProducts.AddRange(off, on);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var rows = await db.CatalogProducts.AsNoTracking().ToDictionaryAsync(p => p.Name);
        Assert.True(rows["Legacy"].UsesSchedule);
        Assert.False(rows["Off"].UsesSchedule);
        Assert.True(rows["On"].UsesSchedule);
        Assert.True(rows["Legacy"].IsActive);
        Assert.False(rows["Off"].IsActive);
        Assert.True(rows["On"].IsActive);
    }
}
