using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Taxonomy.Dtos;
using Platform.Api.Modules.Taxonomy.Services;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Catalog.Services;

/// <summary>Catalog owns its typed assignments, filtering and consumer discovery.</summary>
public static class CatalogTaxonomy
{
    public static async Task ValidateAsync(AppDbContext db, Guid tenant, TaxonomyAssignmentRequest? request, CancellationToken ct)
    {
        if (request is null) return;
        if (request.CategoryIds is null || request.TagIds is null)
            throw new TaxonomyException("Both categoryIds and tagIds are required.");
        var categories = request.CategoryIds.Distinct().ToArray();
        var tags = request.TagIds.Distinct().ToArray();
        if (categories.Length > 20 || tags.Length > 20) throw new TaxonomyException("At most 20 distinct IDs per kind are allowed.");
        if (await db.TaxonomyCategories.CountAsync(x => x.TenantId == tenant && categories.Contains(x.Id), ct) != categories.Length
            || await db.TaxonomyTags.CountAsync(x => x.TenantId == tenant && tags.Contains(x.Id), ct) != tags.Length)
            throw new TaxonomyException("Invalid taxonomy reference.");
    }

    public static async Task ReplaceAsync(AppDbContext db, Guid tenant, Guid product, TaxonomyAssignmentRequest? request, CancellationToken ct)
    {
        if (request is null) return;
        // Diff instead of remove/re-add to avoid duplicate tracked composite keys.
        var categories = request.CategoryIds!.ToHashSet();
        var tags = request.TagIds!.ToHashSet();
        var oldCategories = await db.CatalogProductCategories.Where(x => x.TenantId == tenant && x.ProductId == product).ToListAsync(ct);
        var oldTags = await db.CatalogProductTags.Where(x => x.TenantId == tenant && x.ProductId == product).ToListAsync(ct);
        db.CatalogProductCategories.RemoveRange(oldCategories.Where(x => !categories.Contains(x.CategoryId)));
        db.CatalogProductTags.RemoveRange(oldTags.Where(x => !tags.Contains(x.TagId)));
        foreach (var id in categories.Except(oldCategories.Select(x => x.CategoryId)))
            db.CatalogProductCategories.Add(new() { TenantId = tenant, ProductId = product, CategoryId = id });
        foreach (var id in tags.Except(oldTags.Select(x => x.TagId)))
            db.CatalogProductTags.Add(new() { TenantId = tenant, ProductId = product, TagId = id });
    }

    public static async Task<IQueryable<CatalogProduct>> FilterAsync(AppDbContext db, Guid tenant,
        IQueryable<CatalogProduct> products, TaxonomyFilter? filter, CancellationToken ct)
    {
        products = products.Where(x => x.TenantId == tenant);
        if (filter?.CategoryId is { } category)
        {
            var tree = await db.TaxonomyCategories.AsNoTracking().Where(x => x.TenantId == tenant)
                .Select(x => new { x.Id, x.ParentId }).ToListAsync(ct);
            var descendants = new HashSet<Guid>();
            if (tree.Any(x => x.Id == category)) descendants.Add(category);
            bool added;
            do
            {
                added = false;
                foreach (var node in tree)
                    if (node.ParentId is { } parent && descendants.Contains(parent)) added |= descendants.Add(node.Id);
            } while (added);
            var ids = descendants.ToArray();
            products = products.Where(p => db.CatalogProductCategories.Any(x => x.TenantId == tenant && x.ProductId == p.Id && ids.Contains(x.CategoryId)));
        }
        foreach (var tag in filter?.TagIds.Distinct() ?? [])
        {
            var id = tag;
            products = products.Where(p => db.CatalogProductTags.Any(x => x.TenantId == tenant && x.ProductId == p.Id && x.TagId == id));
        }
        return products;
    }

    public static async Task<Dictionary<Guid, TaxonomyResponse>> LoadAsync(AppDbContext db, Guid tenant, IEnumerable<Guid> productIds, CancellationToken ct)
    {
        var ids = productIds.Distinct().ToArray();
        if (ids.Length == 0) return [];
        var categories = await (from link in db.CatalogProductCategories.AsNoTracking()
            join definition in db.TaxonomyCategories.AsNoTracking() on new { link.TenantId, Id = link.CategoryId } equals new { definition.TenantId, definition.Id }
            where link.TenantId == tenant && definition.TenantId == tenant && ids.Contains(link.ProductId)
            orderby definition.SortOrder, definition.Name, definition.Id
            select new { link.ProductId, Definition = definition }).ToListAsync(ct);
        var tags = await (from link in db.CatalogProductTags.AsNoTracking()
            join definition in db.TaxonomyTags.AsNoTracking() on new { link.TenantId, Id = link.TagId } equals new { definition.TenantId, definition.Id }
            where link.TenantId == tenant && definition.TenantId == tenant && ids.Contains(link.ProductId)
            orderby definition.Name, definition.Id
            select new { link.ProductId, Definition = definition }).ToListAsync(ct);
        var categoriesByProduct = categories.ToLookup(x => x.ProductId, x => TaxonomyService.Map(x.Definition));
        var tagsByProduct = tags.ToLookup(x => x.ProductId, x => TaxonomyService.Map(x.Definition));
        return ids.ToDictionary(id => id, id => new TaxonomyResponse(categoriesByProduct[id].ToArray(), tagsByProduct[id].ToArray()));
    }
}
