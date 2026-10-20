using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Modules.Taxonomy.Dtos;
using Platform.Api.Modules.Taxonomy.Services;
using Platform.Api.Tests.Catalog;
using Platform.Core.Domain.Entities;

namespace Platform.Api.Tests.Taxonomy;

public sealed class CatalogTaxonomyTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Theory]
    [InlineData("bad", null)]
    [InlineData(null, "bad")]
    [InlineData(null, ",")]
    [InlineData(" ", null)]
    public void Malformed_filters_are_invalid(string? category, string? tags) =>
        Assert.Throws<TaxonomyException>(() => TaxonomyFilter.Parse(category, tags));

    [Fact]
    public void Filters_deduplicate_and_limit_distinct_tags()
    {
        var id = Guid.NewGuid();
        Assert.Single(TaxonomyFilter.Parse("", $"{id},{id}").TagIds);
        Assert.Empty(TaxonomyFilter.Parse(null, "").TagIds);
        Assert.Null(TaxonomyFilter.Parse("", null).CategoryId);
        Assert.Throws<TaxonomyException>(() => TaxonomyFilter.Parse(null, $"{id},"));
        Assert.Throws<TaxonomyException>(() => TaxonomyFilter.Parse(null, $",{id}"));
        Assert.Throws<TaxonomyException>(() => TaxonomyFilter.Parse(null, string.Join(',', Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()))));
        Assert.Single(TaxonomyFilter.Parse(null, string.Join(',', Enumerable.Repeat(id, 30))).TagIds);
    }

    [Fact]
    public async Task Descendants_tags_AND_and_existing_filters_combine_and_preserve_direct_metadata()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var service = new TaxonomyService(h.Db, h.TenantProvider);
        var root = await service.CreateCategoryAsync(new("Root", "root"), Ct);
        var child = await service.CreateCategoryAsync(new("Child", "child", root.Id), Ct);
        var leaf = await service.CreateCategoryAsync(new("Leaf", "leaf", child.Id), Ct);
        var a = await service.CreateTagAsync(new("A", "a"), Ct);
        var b = await service.CreateTagAsync(new("B", "b"), Ct);
        var match = await h.Products.CreateAsync(new() { Name = "Café", Code = "REF", Taxonomy = Assignment([leaf.Id, child.Id], [a.Id, b.Id]) }, Ct);
        await h.Products.CreateAsync(new() { Name = "Partial", Taxonomy = Assignment([root.Id], [a.Id]) }, Ct);
        var inactive = await h.Products.CreateAsync(new() { Name = "Café Inactive", Taxonomy = Assignment([leaf.Id], [a.Id, b.Id]) }, Ct);
        await h.Products.DeactivateAsync(inactive.Id, Ct);
        var filter = new TaxonomyFilter(root.Id, [a.Id, b.Id]);
        var staff = await h.Products.ListAsync(new("Café", "REF", true, filter), Ct);
        Assert.Equal(match.Id, Assert.Single(staff).Id);
        Assert.Equal(2, staff[0].Taxonomy.Categories.Count);
        Assert.DoesNotContain(staff[0].Taxonomy.Categories, x => x.Id == root.Id);
        Assert.Equal(match.Id, Assert.Single(await h.Portal.ListProductsAsync("cafe", Ct, filter)).Id);
        Assert.Equal(match.Id, (await h.Portal.GetProductAsync(match.Id, Ct))!.Id);
        Assert.Null(await h.Portal.GetProductAsync(inactive.Id, Ct));
        Assert.Equal(inactive.Id, Assert.Single(await h.Products.ListAsync(new(null, null, false, filter), Ct)).Id);
        Assert.Empty(await h.Products.ListAsync(new(null, null, null, new(Guid.NewGuid(), [])), Ct));
        Assert.Empty(await h.Portal.ListProductsAsync(null, Ct, new(null, [Guid.NewGuid()])));
        Assert.Empty(await h.Products.ListAsync(new(null, null, null, new(null, [a.Id, Guid.NewGuid()])), Ct));
    }

    [Fact]
    public async Task Portal_options_only_expose_active_assignments_and_ancestor_closure()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var service = new TaxonomyService(h.Db, h.TenantProvider);
        var root = await service.CreateCategoryAsync(new("Root", "root"), Ct);
        var child = await service.CreateCategoryAsync(new("Child", "child", root.Id), Ct);
        var unused = await service.CreateCategoryAsync(new("Unused", "unused"), Ct);
        var activeTag = await service.CreateTagAsync(new("Active", "active"), Ct);
        var hiddenTag = await service.CreateTagAsync(new("Hidden", "hidden"), Ct);
        var active = await h.Products.CreateAsync(new() { Name = "Active", Taxonomy = Assignment([child.Id], [activeTag.Id]) }, Ct);
        var hidden = await h.Products.CreateAsync(new() { Name = "Inactive", Taxonomy = Assignment([unused.Id], [hiddenTag.Id]) }, Ct);
        await h.Products.DeactivateAsync(hidden.Id, Ct);
        h.Db.TaxonomyTags.Add(new() { TenantId = Guid.NewGuid(), Name = "Foreign", Slug = "foreign", NormalizedName = "foreign" });
        await h.Db.SaveChangesAsync();
        var options = new CatalogTaxonomyOptionsService(h.Db, h.TenantProvider);
        var portal = await options.GetAsync(true, Ct);
        Assert.Equal(new[] { root.Id, child.Id }.Order(), portal.Categories.Select(x => x.Id).Order());
        Assert.Equal(activeTag.Id, Assert.Single(portal.Tags).Id);
        var staff = await options.GetAsync(false, Ct);
        Assert.Equal(3, staff.Categories.Count);
        Assert.Equal(2, staff.Tags.Count);
        h.TenantProvider.TenantId = Guid.NewGuid();
        Assert.Empty((await options.GetAsync(true, Ct)).Categories);
        Assert.Empty(await h.Portal.ListProductsAsync(null, Ct, new(root.Id, [activeTag.Id])));
    }

    private static TaxonomyAssignmentRequest Assignment(Guid[] categories, Guid[] tags) => new() { CategoryIds = categories, TagIds = tags };
}
