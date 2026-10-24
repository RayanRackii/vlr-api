using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Storage;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;

namespace Platform.Api.Tests.Catalog;

public sealed class CatalogPortalSearchPostgresTests(PostgresContainerFixture postgres)
    : IClassFixture<PostgresContainerFixture>
{
    [DockerFact]
    public async Task Search_with_and_without_unaccent_preserves_literal_matching_and_scope()
    {
        var factory = postgres.Factory ?? throw new InvalidOperationException("PostgreSQL fixture was not started.");
        var tenant = new Tenant("Catalog search", "33333333000191", subdomain: "catalog-search");
        var otherTenant = new Tenant("Other catalog", "44444444000191", subdomain: "other-catalog-search");
        var provider = new FakeTenantProvider { TenantId = tenant.Id };
        await using var db = factory.Create(provider);
        db.Tenants.AddRange(tenant, otherTenant);
        var match = new CatalogProduct { TenantId = tenant.Id, Name = "Café AÇÃO", Code = @"Réf-10%_\01" };
        db.CatalogProducts.AddRange(match,
            new CatalogProduct { TenantId = tenant.Id, Name = "Sem correspondência", Code = "ZZ-100xx01" },
            new CatalogProduct { TenantId = tenant.Id, Name = match.Name, Code = match.Code + "-inactive", IsActive = false },
            new CatalogProduct { TenantId = otherTenant.Id, Name = match.Name, Code = match.Code });
        await db.SaveChangesAsync();

        await using var harness = await CatalogHarness.CreateAsync();
        var portal = new CatalogPortalService(db, provider,
            new DevStorageProvider(new FakeHostEnvironment(), Options.Create(new StorageOptions())),
            Options.Create(new StorageOptions()), harness.Publisher.AsDomainEvents(), harness.Scheduler);

        // This fixture owns a disposable test database; no application database is modified.
        await db.Database.ExecuteSqlRawAsync("DROP EXTENSION IF EXISTS unaccent");
        await AssertSearchAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION unaccent WITH SCHEMA public");
        try
        {
            await AssertSearchAsync();
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DROP EXTENSION unaccent");
        }

        async Task AssertSearchAsync()
        {
            foreach (var search in new[] { "cafe", "CAFÉ", " ACAO ", "ac\u0327a\u0303o", "REF", "réF", "%", "_", @"\", @"10%_\" })
            {
                var result = await portal.ListProductsAsync(search, CancellationToken.None);
                Assert.Equal(match.Id, Assert.Single(result).Id);
            }

            Assert.Empty(await portal.ListProductsAsync("inexistente", CancellationToken.None));
        }
    }
}
