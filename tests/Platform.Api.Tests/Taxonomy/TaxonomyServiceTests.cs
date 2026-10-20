using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Modules.Taxonomy.Dtos;
using Platform.Api.Modules.Taxonomy.Services;
using Platform.Api.Tests.Catalog;
using Platform.Core.Domain.Entities;

namespace Platform.Api.Tests.Taxonomy;

public sealed class TaxonomyServiceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Hierarchy_rejects_fourth_level_self_cycles_and_subtree_overflow()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var service = new TaxonomyService(h.Db, h.TenantProvider);
        var root = await service.CreateCategoryAsync(new("Root", "root"), Ct);
        var child = await service.CreateCategoryAsync(new("Child", "child", root.Id), Ct);
        var leaf = await service.CreateCategoryAsync(new("Leaf", "leaf", child.Id), Ct);
        await AssertInvalid(() => service.CreateCategoryAsync(new("Fourth", "fourth", leaf.Id), Ct));
        await AssertInvalid(() => service.UpdateCategoryAsync(root.Id, new("Root", root.Id, null, 0), Ct));
        await AssertInvalid(() => service.UpdateCategoryAsync(root.Id, new("Root", leaf.Id, null, 0), Ct));
        var other = await service.CreateCategoryAsync(new("Other", "other"), Ct);
        await AssertInvalid(() => service.UpdateCategoryAsync(root.Id, new("Root", other.Id, null, 0), Ct));
        Assert.Null((await service.CategoryAsync(root.Id, Ct))!.ParentId);
        Assert.Equal(4, (await service.CategoriesAsync(Ct)).Count);
    }

    [Theory]
    [InlineData("  CAFÉ\t  Azul ", "café azul")]
    [InlineData("cafe\u0301\nAzul", "café azul")]
    [InlineData("A\u00a0B", "a b")]
    public void Tag_normalization_uses_NFC_whitespace_and_invariant_case(string input, string expected) =>
        Assert.Equal(expected, TaxonomyService.NormalizeTagName(input));

    [Fact]
    public async Task Duplicate_slugs_and_normalized_names_are_tenant_scoped_and_kinds_independent()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var service = new TaxonomyService(h.Db, h.TenantProvider);
        var tag = await service.CreateTagAsync(new(" Café  Azul ", "cafe"), Ct);
        await AssertCode("TAXONOMY_DUPLICATE", () => service.CreateTagAsync(new("CAFE\u0301\tAzul", "other"), Ct));
        await AssertCode("TAXONOMY_DUPLICATE", () => service.CreateTagAsync(new("Other", "cafe"), Ct));
        var category = await service.CreateCategoryAsync(new("Repeated", "cafe"), Ct);
        await service.CreateCategoryAsync(new("Repeated", "different"), Ct);
        await AssertCode("TAXONOMY_DUPLICATE", () => service.CreateCategoryAsync(new("Child", "cafe", category.Id), Ct));
        h.TenantProvider.TenantId = Guid.NewGuid();
        await service.CreateTagAsync(new("Café Azul", "cafe"), Ct);
        h.TenantProvider.TenantId = h.Tenant.Id;
        Assert.Equal(tag.Id, Assert.Single(await service.TagsAsync(Ct)).Id);
    }

    [Theory]
    [InlineData("UPPER")]
    [InlineData("with space")]
    [InlineData("café")]
    [InlineData("-prefix")]
    [InlineData("two--dash")]
    [InlineData("suffix-")]
    [InlineData("slug\n")]
    [InlineData("")]
    public async Task Invalid_slugs_are_rejected(string slug)
    {
        await using var h = await CatalogHarness.CreateAsync();
        await AssertInvalid(() => new TaxonomyService(h.Db, h.TenantProvider).CreateTagAsync(new("Name", slug), Ct));
    }

    [Fact]
    public async Task Validates_names_icon_sort_and_immutable_slugs()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var service = new TaxonomyService(h.Db, h.TenantProvider);
        await AssertInvalid(() => service.CreateCategoryAsync(new(" ", "blank"), Ct));
        await AssertInvalid(() => service.CreateCategoryAsync(new(new string('x', 101), "long"), Ct));
        await AssertInvalid(() => service.CreateTagAsync(new(new string('x', 51), "long"), Ct));
        await AssertInvalid(() => service.CreateTagAsync(new("X", new string('x', 81)), Ct));
        await AssertInvalid(() => service.CreateCategoryAsync(new("X", "x", Icon: "<svg/>"), Ct));
        await AssertInvalid(() => service.CreateCategoryAsync(new("X", "x", SortOrder: -1), Ct));
        await AssertInvalid(() => service.CreateCategoryAsync(new("X", "x", SortOrder: 100001), Ct));
        var category = await service.CreateCategoryAsync(new(" Root ", "root", Icon: "folder", SortOrder: 100000), Ct);
        var request = JsonSerializer.Deserialize<UpdateCategoryRequest>("""{"name":"Changed","slug":"changed","parentId":null,"icon":null,"sortOrder":0}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        await AssertInvalid(() => service.UpdateCategoryAsync(category.Id, request, Ct));
        var tag = await service.CreateTagAsync(new("Tag", "tag"), Ct);
        var tagUpdate = JsonSerializer.Deserialize<UpdateTagRequest>("""{"name":"Tag","slug":"changed"}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        await AssertInvalid(() => service.UpdateTagAsync(tag.Id, tagUpdate, Ct));
        var updated = await service.UpdateCategoryAsync(category.Id, new("Renamed", null, null, 0), Ct);
        Assert.Equal("root", updated!.Slug);
        Assert.Null(updated.Icon);
    }

    [Fact]
    public async Task Delete_rejects_children_and_assignments_then_allows_unused_definitions()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var service = new TaxonomyService(h.Db, h.TenantProvider);
        var root = await service.CreateCategoryAsync(new("Root", "root"), Ct);
        var child = await service.CreateCategoryAsync(new("Child", "child", root.Id), Ct);
        var tag = await service.CreateTagAsync(new("Tag", "tag"), Ct);
        await AssertCode("TAXONOMY_IN_USE", () => service.DeleteCategoryAsync(root.Id, Ct));
        var p = await h.Products.CreateAsync(new() { Name = "Product", Taxonomy = new() { CategoryIds = [child.Id], TagIds = [tag.Id] } }, Ct);
        await AssertCode("TAXONOMY_IN_USE", () => service.DeleteCategoryAsync(child.Id, Ct));
        await AssertCode("TAXONOMY_IN_USE", () => service.DeleteTagAsync(tag.Id, Ct));
        await h.Products.UpdateAsync(p.Id, new() { Name = p.Name, Taxonomy = new() { CategoryIds = [], TagIds = [] } }, Ct);
        Assert.True(await service.DeleteCategoryAsync(child.Id, Ct));
        Assert.True(await service.DeleteCategoryAsync(root.Id, Ct));
        Assert.True(await service.DeleteTagAsync(tag.Id, Ct));
        Assert.False(await service.DeleteTagAsync(tag.Id, Ct));
    }

    [Fact]
    public async Task Tenant_absence_fails_closed_and_foreign_references_equal_missing_references()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var service = new TaxonomyService(h.Db, h.TenantProvider);
        var category = await service.CreateCategoryAsync(new("Root", "root"), Ct);
        var tag = await service.CreateTagAsync(new("Tag", "tag"), Ct);
        h.TenantProvider.TenantId = Guid.NewGuid();
        Assert.Null(await service.CategoryAsync(category.Id, Ct));
        Assert.Null(await service.TagAsync(tag.Id, Ct));
        Assert.Null(await service.UpdateCategoryAsync(category.Id, new("X", null, null, 0), Ct));
        Assert.False(await service.DeleteCategoryAsync(category.Id, Ct));
        await AssertInvalid(() => service.CreateCategoryAsync(new("X", "x", category.Id), Ct));
        await AssertInvalid(() => service.CreateCategoryAsync(new("X", "x", Guid.NewGuid()), Ct));
        await AssertInvalid(() => h.Products.CreateAsync(new() { Name = "X", Taxonomy = new() { CategoryIds = [category.Id], TagIds = [tag.Id] } }, Ct));
        h.TenantProvider.TenantId = null;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CategoriesAsync(Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.TagsAsync(Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CategoryAsync(category.Id, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.TagAsync(tag.Id, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateTagAsync(new("X", "x"), Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpdateTagAsync(tag.Id, new("X"), Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DeleteTagAsync(tag.Id, Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new CatalogTaxonomyOptionsService(h.Db, h.TenantProvider).GetAsync(true, Ct));
    }

    [Fact]
    public async Task Omitted_null_empty_and_duplicate_assignments_preserve_old_requests()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var service = new TaxonomyService(h.Db, h.TenantProvider);
        var category = await service.CreateCategoryAsync(new("Root", "root"), Ct);
        var tag = await service.CreateTagAsync(new("Tag", "tag"), Ct);
        var legacy = await h.Products.CreateAsync(new() { Name = "Legacy" }, Ct);
        Assert.Empty(legacy.Taxonomy.Categories);
        Assert.Empty(legacy.Taxonomy.Tags);
        var p = await h.Products.CreateAsync(new() { Name = "Product", Taxonomy = new() { CategoryIds = [category.Id, category.Id], TagIds = [tag.Id, tag.Id] } }, Ct);
        Assert.Single(p.Taxonomy.Categories);
        Assert.Single(p.Taxonomy.Tags);
        var omitted = JsonSerializer.Deserialize<UpdateCatalogProductRequest>("""{"name":"Omitted"}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var explicitNull = JsonSerializer.Deserialize<UpdateCatalogProductRequest>("""{"name":"Null","taxonomy":null}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        foreach (var request in new[] { omitted, explicitNull })
        {
            var updated = await h.Products.UpdateAsync(p.Id, request, Ct);
            Assert.Single(updated!.Taxonomy.Categories);
            Assert.Single(updated.Taxonomy.Tags);
        }
        Assert.Single((await h.Products.DeactivateAsync(p.Id, Ct))!.Taxonomy.Tags);
        Assert.Single((await h.Products.ActivateAsync(p.Id, Ct))!.Taxonomy.Categories);
        var cleared = await h.Products.UpdateAsync(p.Id, new() { Name = "Clear", Taxonomy = new() { CategoryIds = [], TagIds = [] } }, Ct);
        Assert.Empty(cleared!.Taxonomy.Categories);
        Assert.Empty(cleared.Taxonomy.Tags);
    }

    [Fact]
    public async Task Invalid_assignment_does_not_modify_product_or_links()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var tag = await new TaxonomyService(h.Db, h.TenantProvider).CreateTagAsync(new("Tag", "tag"), Ct);
        var p = await h.Products.CreateAsync(new() { Name = "Original", Price = 10, Taxonomy = new() { CategoryIds = [], TagIds = [tag.Id] } }, Ct);
        foreach (var assignment in new[] {
            new TaxonomyAssignmentRequest(), new() { CategoryIds = [] },
            new() { CategoryIds = [], TagIds = [Guid.NewGuid()] },
            new() { CategoryIds = Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToArray(), TagIds = [] } })
        {
            await AssertInvalid(() => h.Products.UpdateAsync(p.Id, new() { Name = "Changed", Price = 99, Taxonomy = assignment }, Ct));
            h.Db.ChangeTracker.Clear();
            var unchanged = await h.Products.GetByIdAsync(p.Id, Ct);
            Assert.Equal("Original", unchanged!.Name);
            Assert.Equal(10, unchanged.Price);
            Assert.Equal(tag.Id, Assert.Single(unchanged.Taxonomy.Tags).Id);
        }
        await AssertInvalid(() => h.Products.CreateAsync(new() { Name = "Invalid", Taxonomy = new() { CategoryIds = [], TagIds = [Guid.NewGuid()] } }, Ct));
        Assert.Equal(1, await h.Db.CatalogProducts.CountAsync());
    }

    private static async Task AssertInvalid(Func<Task> action) => await AssertCode("TAXONOMY_INVALID", action);
    private static async Task AssertCode(string code, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<TaxonomyException>(action);
        Assert.Equal(code, ex.Code);
        Assert.Equal(code == "TAXONOMY_INVALID" ? 400 : 409, ex.Status);
    }
}
