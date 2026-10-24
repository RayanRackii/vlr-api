using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Modules.Taxonomy.Dtos;
using Platform.Api.Modules.Taxonomy.Services;
using Platform.Api.Storage;
using Platform.Api.Tests.Catalog;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Taxonomy;

public sealed class TaxonomyPostgresTests(PostgresContainerFixture postgres) : IClassFixture<PostgresContainerFixture>
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private PostgresAppDbFactory Factory => postgres.Factory ?? throw new InvalidOperationException("PostgreSQL fixture unavailable.");

    [DockerFact]
    public async Task Composite_FKs_prevent_foreign_product_definition_and_parent_references()
    {
        var (tenant, other) = await SeedTenantsAsync();
        Guid productId, foreignProductId, categoryId, tagId;
        await using (var seed = Factory.Create(new FakeTenantProvider()))
        {
            var product = new CatalogProduct { TenantId = tenant, Name = "Product" };
            var foreign = new CatalogProduct { TenantId = other, Name = "Other" };
            var category = new TaxonomyCategory { TenantId = other, Name = "Other", Slug = "other" };
            var tag = new TaxonomyTag { TenantId = other, Name = "Other", Slug = "other", NormalizedName = "other" };
            seed.AddRange(product, foreign, category, tag);
            await seed.SaveChangesAsync();
            (productId, foreignProductId, categoryId, tagId) = (product.Id, foreign.Id, category.Id, tag.Id);
        }
        await AssertForeignKeyAsync(db => db.TaxonomyCategories.Add(new() { TenantId = tenant, Name = "Child", Slug = "child", ParentId = categoryId }));
        await AssertForeignKeyAsync(db => db.CatalogProductCategories.Add(new() { TenantId = tenant, ProductId = productId, CategoryId = categoryId }));
        await AssertForeignKeyAsync(db => db.CatalogProductTags.Add(new() { TenantId = tenant, ProductId = productId, TagId = tagId }));
        await AssertForeignKeyAsync(db => db.CatalogProductTags.Add(new() { TenantId = other, ProductId = productId, TagId = tagId }));
        await AssertForeignKeyAsync(db => db.CatalogProductCategories.Add(new() { TenantId = other, ProductId = Guid.NewGuid(), CategoryId = categoryId }));

        async Task AssertForeignKeyAsync(Action<AppDbContext> add)
        {
            await using var db = Factory.Create(new FakeTenantProvider());
            add(db);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
    }

    [DockerFact]
    public async Task Unique_indexes_enforce_slug_and_normalized_name_races_and_service_translates_conflicts()
    {
        var (tenant, _) = await SeedTenantsAsync();
        var attempts = await Task.WhenAll(InsertTagAsync("same", "one"), InsertTagAsync("same", "two"));
        Assert.Single(attempts, x => x is null);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.Single(attempts.OfType<PostgresException>()).SqlState);
        var slugs = await Task.WhenAll(InsertTagAsync("different-one", "shared"), InsertTagAsync("different-two", "shared"));
        Assert.Single(slugs, x => x is null);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.Single(slugs.OfType<PostgresException>()).SqlState);
        var categories = await Task.WhenAll(CreateCategoryAsync(), CreateCategoryAsync());
        Assert.Single(categories, x => x is null);
        Assert.Equal("TAXONOMY_DUPLICATE", Assert.Single(categories.OfType<TaxonomyException>()).Code);

        async Task<PostgresException?> InsertTagAsync(string normalized, string slug)
        {
            await using var db = Factory.Create(new FakeTenantProvider { TenantId = tenant });
            db.TaxonomyTags.Add(new() { TenantId = tenant, Name = normalized, NormalizedName = normalized, Slug = slug });
            try { await db.SaveChangesAsync(); return null; }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg) { return pg; }
        }
        async Task<TaxonomyException?> CreateCategoryAsync()
        {
            var provider = new FakeTenantProvider { TenantId = tenant };
            await using var db = Factory.Create(provider);
            try { await new TaxonomyService(db, provider).CreateCategoryAsync(new("Category", "same-category"), Ct); return null; }
            catch (TaxonomyException ex) { return ex; }
        }
    }

    [DockerFact]
    public async Task Concurrent_reparenting_cannot_create_a_cycle_or_overdeep_subtree()
    {
        var (tenant, _) = await SeedTenantsAsync();
        var provider = new FakeTenantProvider { TenantId = tenant };
        Guid a, b;
        await using (var db = Factory.Create(provider))
        {
            var service = new TaxonomyService(db, provider);
            a = (await service.CreateCategoryAsync(new("A", "a"), Ct)).Id;
            b = (await service.CreateCategoryAsync(new("B", "b"), Ct)).Id;
        }
        var results = await Task.WhenAll(ReparentAsync(a, b), ReparentAsync(b, a));
        Assert.Single(results, x => x);
        await using var verify = Factory.Create(provider);
        TaxonomyService.ValidateHierarchy(await verify.TaxonomyCategories.ToListAsync());

        async Task<bool> ReparentAsync(Guid id, Guid parent)
        {
            await using var db = Factory.Create(provider);
            try { await new TaxonomyService(db, provider).UpdateCategoryAsync(id, new("Name", parent, null, 0), Ct); return true; }
            catch (TaxonomyException ex) { Assert.Equal("TAXONOMY_INVALID", ex.Code); return false; }
        }
    }

    [DockerFact]
    public async Task Concurrent_delete_and_assignment_leave_valid_state_and_never_drop_existing_links()
    {
        var (tenant, _) = await SeedTenantsAsync();
        var provider = new FakeTenantProvider { TenantId = tenant };
        Guid tag, product;
        await using (var db = Factory.Create(provider))
        {
            tag = (await new TaxonomyService(db, provider).CreateTagAsync(new("Tag", "tag"), Ct)).Id;
            product = (await Products(db, provider).CreateAsync(new() { Name = "Original" }, Ct)).Id;
        }
        var results = await Task.WhenAll(DeleteAsync(), AssignAsync());
        Assert.Single(results, x => x);
        await using var verify = Factory.Create(provider);
        var links = await verify.CatalogProductTags.ToListAsync();
        var exists = await verify.TaxonomyTags.AnyAsync(x => x.Id == tag);
        Assert.Equal(exists ? 1 : 0, links.Count);
        var saved = await verify.CatalogProducts.SingleAsync(x => x.Id == product);
        Assert.Equal(exists ? "Assigned" : "Original", saved.Name);

        async Task<bool> DeleteAsync()
        {
            await using var db = Factory.Create(provider);
            try { return await new TaxonomyService(db, provider).DeleteTagAsync(tag, Ct); }
            catch (TaxonomyException ex) { Assert.Equal("TAXONOMY_IN_USE", ex.Code); return false; }
        }
        async Task<bool> AssignAsync()
        {
            await using var db = Factory.Create(provider);
            try { await Products(db, provider).UpdateAsync(product, new() { Name = "Assigned", Taxonomy = new() { CategoryIds = [], TagIds = [tag] } }, Ct); return true; }
            catch (TaxonomyException ex) { Assert.Equal("TAXONOMY_INVALID", ex.Code); return false; }
        }
    }

    [DockerFact]
    public async Task Save_failure_rolls_back_product_and_assignment_changes_atomically()
    {
        var (tenant, _) = await SeedTenantsAsync();
        var provider = new FakeTenantProvider { TenantId = tenant };
        Guid product, a, b;
        await using (var db = Factory.Create(provider))
        {
            var taxonomy = new TaxonomyService(db, provider);
            a = (await taxonomy.CreateTagAsync(new("A", "a"), Ct)).Id;
            b = (await taxonomy.CreateTagAsync(new("B", "b"), Ct)).Id;
            product = (await Products(db, provider).CreateAsync(new() { Name = "Original", Taxonomy = new() { CategoryIds = [], TagIds = [a] } }, Ct)).Id;
        }
        // Failure after the INSERT/UPDATE/DELETE statements, before transaction commit.
        await using (var failing = Factory.Create(provider, new FailAfterSave()))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Products(failing, provider).UpdateAsync(product,
                new() { Name = "Changed", Taxonomy = new() { CategoryIds = [], TagIds = [b] } }, Ct));
        }
        await using var verify = Factory.Create(provider);
        Assert.Equal("Original", (await verify.CatalogProducts.SingleAsync(x => x.Id == product)).Name);
        Assert.Equal(a, (await verify.CatalogProductTags.SingleAsync(x => x.ProductId == product)).TagId);
    }

    [DockerFact]
    public async Task Product_delete_cascades_joins_but_used_definitions_are_restricted()
    {
        var (tenant, _) = await SeedTenantsAsync();
        var provider = new FakeTenantProvider { TenantId = tenant };
        await using var db = Factory.Create(provider);
        var category = await new TaxonomyService(db, provider).CreateCategoryAsync(new("Category", "category"), Ct);
        var tag = await new TaxonomyService(db, provider).CreateTagAsync(new("Tag", "tag"), Ct);
        var product = await Products(db, provider).CreateAsync(new() { Name = "P", Taxonomy = new() { CategoryIds = [category.Id], TagIds = [tag.Id] } }, Ct);
        db.ChangeTracker.Clear();
        db.TaxonomyTags.Remove(await db.TaxonomyTags.SingleAsync(x => x.Id == tag.Id));
        var error = await Assert.ThrowsAsync<TaxonomyException>(() => TaxonomyService.SaveAsync(db, Ct));
        Assert.Equal("TAXONOMY_IN_USE", error.Code);
        db.ChangeTracker.Clear();
        await db.CatalogProducts.Where(x => x.Id == product.Id).ExecuteDeleteAsync();
        Assert.Empty(await db.CatalogProductCategories.ToListAsync());
        Assert.Empty(await db.CatalogProductTags.ToListAsync());
        Assert.Single(await db.TaxonomyCategories.ToListAsync());
        Assert.Single(await db.TaxonomyTags.ToListAsync());
    }

    [DockerFact]
    public async Task SQL_filters_apply_before_both_portal_search_branches_and_batch_load_metadata()
    {
        var (tenant, other) = await SeedTenantsAsync();
        var provider = new FakeTenantProvider { TenantId = tenant };
        await using var db = Factory.Create(provider);
        var service = new TaxonomyService(db, provider);
        var root = await service.CreateCategoryAsync(new("Root", "root"), Ct);
        var leaf = await service.CreateCategoryAsync(new("Leaf", "leaf", root.Id), Ct);
        var a = await service.CreateTagAsync(new("A", "a"), Ct);
        var b = await service.CreateTagAsync(new("B", "b"), Ct);
        var assignment = new TaxonomyAssignmentRequest { CategoryIds = [leaf.Id], TagIds = [a.Id, b.Id] };
        var products = Products(db, provider);
        var match = await products.CreateAsync(new() { Name = "Café AÇÃO", Taxonomy = assignment }, Ct);
        await products.CreateAsync(new() { Name = "Café partial", Taxonomy = new() { CategoryIds = [leaf.Id], TagIds = [a.Id] } }, Ct);
        var inactive = await products.CreateAsync(new() { Name = "Café inactive", Taxonomy = assignment }, Ct);
        await products.DeactivateAsync(inactive.Id, Ct);
        db.CatalogProducts.Add(new() { TenantId = other, Name = "Café foreign" });
        await db.SaveChangesAsync();
        await using var h = await CatalogHarness.CreateAsync();
        var options = Options.Create(new StorageOptions());
        var portal = new CatalogPortalService(db, provider, new DevStorageProvider(new FakeHostEnvironment(), options), options, h.Publisher.AsDomainEvents(), h.Scheduler);
        await db.Database.ExecuteSqlRawAsync("DROP EXTENSION IF EXISTS unaccent");
        await CheckAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION unaccent WITH SCHEMA public");
        try { await CheckAsync(); }
        finally { await db.Database.ExecuteSqlRawAsync("DROP EXTENSION unaccent"); }

        async Task CheckAsync()
        {
            var result = await portal.ListProductsAsync("cafe", Ct, new(root.Id, [a.Id, b.Id]));
            var p = Assert.Single(result);
            Assert.Equal(match.Id, p.Id);
            Assert.Equal(leaf.Id, Assert.Single(p.Taxonomy.Categories).Id);
            Assert.Equal(2, p.Taxonomy.Tags.Count);
            Assert.Empty(await portal.ListProductsAsync("cafe", Ct, new(Guid.NewGuid(), [a.Id])));
            Assert.Empty(await portal.ListProductsAsync("cafe", Ct, new(root.Id, [a.Id, Guid.NewGuid()])));
            Assert.Equal(match.Id, Assert.Single(await products.ListAsync(new("Café", null, true, new(root.Id, [a.Id, b.Id])), Ct)).Id);
        }
    }

    private async Task<(Guid Tenant, Guid Other)> SeedTenantsAsync()
    {
        await using var db = Factory.Create(new FakeTenantProvider());
        var tenant = new Tenant("Taxonomy", Guid.NewGuid().ToString("N")[..14], subdomain: "tax-" + Guid.NewGuid().ToString("N")[..8]);
        var other = new Tenant("Other", Guid.NewGuid().ToString("N")[..14], subdomain: "tax-" + Guid.NewGuid().ToString("N")[..8]);
        db.Tenants.AddRange(tenant, other);
        await db.SaveChangesAsync();
        return (tenant.Id, other.Id);
    }

    private static CatalogProductService Products(AppDbContext db, FakeTenantProvider provider)
    {
        var options = Options.Create(new StorageOptions());
        return new(db, provider, new DevStorageProvider(new FakeHostEnvironment(), options), options);
    }

    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Injected save failure.");
    }
}
