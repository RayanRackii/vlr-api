using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Taxonomy.Dtos;
using Platform.Api.Modules.Taxonomy.Services;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Catalog.Services;

public sealed class CatalogTaxonomyOptionsService(AppDbContext db, ITenantProvider tenantProvider)
{
    public async Task<TaxonomyResponse> GetAsync(bool portal, CancellationToken ct)
    {
        var tenant = tenantProvider.TenantId ?? throw new UnauthorizedAccessException("Tenant context is required.");
        var categories = await db.TaxonomyCategories.AsNoTracking().Where(x => x.TenantId == tenant)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(ct);
        var tags = db.TaxonomyTags.AsNoTracking().Where(x => x.TenantId == tenant);
        if (portal)
        {
            var active = db.CatalogProducts.Where(x => x.TenantId == tenant && x.IsActive);
            var used = (await db.CatalogProductCategories.Where(x => x.TenantId == tenant && active.Any(p => p.Id == x.ProductId))
                .Select(x => x.CategoryId).Distinct().ToListAsync(ct)).ToHashSet();
            var parents = categories.ToDictionary(x => x.Id, x => x.ParentId);
            foreach (var id in used.ToArray())
            {
                var current = id;
                var seen = new HashSet<Guid>();
                while (seen.Add(current) && parents.TryGetValue(current, out var parent) && parent is { } parentId)
                {
                    used.Add(parentId);
                    current = parentId;
                }
            }
            categories = categories.Where(x => used.Contains(x.Id)).ToList();
            tags = tags.Where(t => db.CatalogProductTags.Any(x => x.TenantId == tenant && x.TagId == t.Id && active.Any(p => p.Id == x.ProductId)));
        }
        return new(categories.Select(TaxonomyService.Map).ToArray(),
            (await tags.OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(ct)).Select(TaxonomyService.Map).ToArray());
    }
}
