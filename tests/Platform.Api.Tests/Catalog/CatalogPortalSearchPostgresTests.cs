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
            Options.Create(new StorageOptions()), harness.Publisher.AsDomainEvents(), harness.Scheduler,
            new Platform.Api.Modules.Rentals.Services.ResourceOccupancyService());

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
                var result = await portal.ListProductsAsync(null, search, CancellationToken.None);
                Assert.Equal(match.Id, Assert.Single(result).Id);
            }

            Assert.Empty(await portal.ListProductsAsync(null, "inexistente", CancellationToken.None));
        }
    }

    [DockerFact]
    public async Task Visibility_filter_applies_on_postgres_in_both_search_branches()
    {
        var factory = postgres.Factory ?? throw new InvalidOperationException("PostgreSQL fixture was not started.");
        var tenant = new Tenant("Catalog vis", "66666666000191", subdomain: "catalog-vis");
        var provider = new FakeTenantProvider { TenantId = tenant.Id };
        await using var db = factory.Create(provider);
        db.Tenants.Add(tenant);
        var pub = new CatalogProduct { TenantId = tenant.Id, Name = "Item público" };
        var direct = new CatalogProduct { TenantId = tenant.Id, Name = "Item direto", Visibility = Platform.Core.Domain.Enums.CatalogProductVisibility.Restricted };
        var viaGroup = new CatalogProduct { TenantId = tenant.Id, Name = "Item grupo", Visibility = Platform.Core.Domain.Enums.CatalogProductVisibility.Restricted };
        var ana = new Customer { TenantId = tenant.Id, Name = "Ana", Email = "ana@vis.test", Phone = "+5511999990000" };
        var bia = new Customer { TenantId = tenant.Id, Name = "Bia", Email = "bia@vis.test", Phone = "+5511999990001" };
        var group = new CustomerGroup { TenantId = tenant.Id, Name = "VIP" };
        db.CatalogProducts.AddRange(pub, direct, viaGroup);
        db.Customers.AddRange(ana, bia);
        db.CustomerGroups.Add(group);
        await db.SaveChangesAsync();
        db.CustomerGroupMembers.Add(new CustomerGroupMember { TenantId = tenant.Id, GroupId = group.Id, CustomerId = bia.Id });
        db.CatalogProductAccess.AddRange(
            new CatalogProductAccess { TenantId = tenant.Id, ProductId = direct.Id, CustomerId = ana.Id },
            new CatalogProductAccess { TenantId = tenant.Id, ProductId = viaGroup.Id, GroupId = group.Id });
        await db.SaveChangesAsync();

        // CHECK constraint: exactly one target.
        db.CatalogProductAccess.Add(new CatalogProductAccess { TenantId = tenant.Id, ProductId = pub.Id, CustomerId = ana.Id, GroupId = group.Id });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        await using var harness = await CatalogHarness.CreateAsync();
        var portal = new CatalogPortalService(db, provider,
            new DevStorageProvider(new FakeHostEnvironment(), Options.Create(new StorageOptions())),
            Options.Create(new StorageOptions()), harness.Publisher.AsDomainEvents(), harness.Scheduler,
            new Platform.Api.Modules.Rentals.Services.ResourceOccupancyService());

        await db.Database.ExecuteSqlRawAsync("DROP EXTENSION IF EXISTS unaccent");
        await AssertAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION unaccent WITH SCHEMA public");
        try { await AssertAsync(); }
        finally { await db.Database.ExecuteSqlRawAsync("DROP EXTENSION unaccent"); }

        async Task AssertAsync()
        {
            foreach (var search in new string?[] { null, "item" })
            {
                async Task<HashSet<Guid>> Ids(Guid? c) => (await portal.ListProductsAsync(c, search, CancellationToken.None)).Select(p => p.Id).ToHashSet();
                Assert.Equal(new HashSet<Guid> { pub.Id }, await Ids(null));
                Assert.Equal(new HashSet<Guid> { pub.Id, direct.Id }, await Ids(ana.Id));
                Assert.Equal(new HashSet<Guid> { pub.Id, viaGroup.Id }, await Ids(bia.Id));
            }
            Assert.Null(await portal.GetProductAsync(bia.Id, direct.Id, CancellationToken.None));
        }
    }

    [DockerFact]
    public async Task Group_service_on_postgres_locks_access_edit_enforces_case_insensitive_name_and_cascades()
    {
        var factory = postgres.Factory ?? throw new InvalidOperationException("PostgreSQL fixture was not started.");
        var tenant = new Tenant("Catalog groups", "77777777000191", subdomain: "catalog-groups");
        var provider = new FakeTenantProvider { TenantId = tenant.Id };
        await using var db = factory.Create(provider);
        db.Tenants.Add(tenant);
        var product = new CatalogProduct { TenantId = tenant.Id, Name = "Item" };
        var ana = new Customer { TenantId = tenant.Id, Name = "Ana", Email = "ana@grp.test", Phone = "+5511999990000" };
        db.CatalogProducts.Add(product);
        db.Customers.Add(ana);
        await db.SaveChangesAsync();
        var groups = new CustomerGroupService(db, provider);

        var g = await groups.CreateAsync(new("Revenda", null), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => groups.CreateAsync(new("REVENDA", null), CancellationToken.None));
        db.ChangeTracker.Clear();
        // DB-level guard even if the pre-check is bypassed.
        db.CustomerGroups.Add(new CustomerGroup { TenantId = tenant.Id, Name = "revenda" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        await groups.AddMembersAsync(g.Id, new([ana.Id]), CancellationToken.None);
        var access = await groups.SetProductAccessAsync(product.Id, new("Restricted", [ana.Id], [g.Id]), CancellationToken.None);
        Assert.False(access!.RestrictedWithoutGrants);
        Assert.Equal(2, await db.CatalogProductAccess.CountAsync(a => a.ProductId == product.Id));

        Assert.True(await groups.DeleteAsync(g.Id, CancellationToken.None));
        Assert.Equal(0, await db.CustomerGroupMembers.CountAsync(m => m.GroupId == g.Id));
        Assert.Equal(1, await db.CatalogProductAccess.CountAsync(a => a.ProductId == product.Id));
        Assert.Equal(4, await db.CatalogAccessAudit.CountAsync(a => a.TenantId == tenant.Id));
    }
}
