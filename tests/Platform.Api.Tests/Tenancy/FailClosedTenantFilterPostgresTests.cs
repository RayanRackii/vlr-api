using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Tenancy;

public sealed class FailClosedTenantFilterPostgresTests(PostgresContainerFixture postgres)
    : IClassFixture<PostgresContainerFixture>
{
    [DockerFact]
    public async Task Relational_filter_matrix_tracks_flag_tenant_and_platform_scope()
    {
        var factory = postgres.Factory ?? throw new InvalidOperationException("PostgreSQL fixture was not started.");
        var a = new Tenant("Tenant A", Guid.NewGuid().ToString("N")[..20]);
        var b = new Tenant("Tenant B", Guid.NewGuid().ToString("N")[..20]);
        var categoryA = new AssetCategory { TenantId = a.Id, Name = "Category A" };
        var categoryB = new AssetCategory { TenantId = b.Id, Name = "Category B" };
        var unitA = new Unit(a.Id, "Unit A");
        var unitB = new Unit(b.Id, "Unit B");
        var productA = new CatalogProduct { TenantId = a.Id, Name = "Product A" };
        var productB = new CatalogProduct { TenantId = b.Id, Name = "Product B" };
        await using (var seed = factory.Create(new FakeTenantProvider(), false))
        {
            seed.Tenants.AddRange(a, b);
            seed.AssetCategories.AddRange(categoryA, categoryB);
            seed.Units.AddRange(unitA, unitB);
            seed.CatalogProducts.AddRange(productA, productB);
            await seed.SaveChangesAsync();
        }

        // Fresh contexts share EF's cached model: the flag and tenant must remain query parameters.
        foreach (var failClosed in new[] { false, true })
        {
            var provider = new FakeTenantProvider();
            await using var db = factory.Create(provider, failClosed);
            var categories = db.AssetCategories.AsNoTracking().Where(x => x.Id == categoryA.Id || x.Id == categoryB.Id);
            var units = db.Units.AsNoTracking().Where(x => x.Id == unitA.Id || x.Id == unitB.Id);
            var products = db.CatalogProducts.AsNoTracking().Where(x => x.Id == productA.Id || x.Id == productB.Id);

            async Task AssertRowsAsync(int count, Guid? owner = null)
            {
                var categoryOwners = await categories.Select(x => x.TenantId).ToListAsync();
                var unitOwners = await units.Select(x => x.TenantId).ToListAsync();
                var productOwners = await products.Select(x => x.TenantId).ToListAsync();
                Assert.Equal(count, categoryOwners.Count);
                Assert.Equal(count, unitOwners.Count);
                Assert.Equal(count, productOwners.Count);
                if (owner.HasValue)
                {
                    Assert.All(categoryOwners.Concat(unitOwners).Concat(productOwners), id => Assert.Equal(owner.Value, id));
                }
            }

            provider.TenantId = a.Id;
            await AssertRowsAsync(1, a.Id);
            provider.TenantId = b.Id;
            await AssertRowsAsync(1, b.Id);
            provider.TenantId = null;
            await AssertRowsAsync(failClosed ? 0 : 2);
            using (PlatformScope.Begin(NullLogger.Instance, "test-relational-filter-matrix"))
            {
                await AssertRowsAsync(2);
            }
            await AssertRowsAsync(failClosed ? 0 : 2);

            // Existing provider tenant is ignored by PlatformScope only with the new flag.
            provider.TenantId = a.Id;
            using (PlatformScope.Begin(NullLogger.Instance, "test-relational-filter-provider-tenant"))
            {
                await AssertRowsAsync(failClosed ? 2 : 1);
            }
            await AssertRowsAsync(1, a.Id);
        }
    }
}
