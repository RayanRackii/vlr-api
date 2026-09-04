using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence;

public static class AssetCategoryExampleSeeder
{
    public static async Task SeedForFamilyIdsAsync(
        AppDbContext dbContext,
        Guid tenantId,
        IReadOnlyCollection<Guid> familyIds,
        CancellationToken cancellationToken)
    {
        if (familyIds.Count == 0)
        {
            return;
        }

        var familyKeys = await dbContext.AssetFamilies
            .AsNoTracking()
            .Where(family => familyIds.Contains(family.Id))
            .Select(family => family.Key)
            .ToListAsync(cancellationToken);

        await SeedForFamilyKeysAsync(dbContext, tenantId, familyKeys, cancellationToken);
    }

    public static async Task SeedForFamilyKeysAsync(
        AppDbContext dbContext,
        Guid tenantId,
        IEnumerable<string> familyKeys,
        CancellationToken cancellationToken)
    {
        var names = new List<string>();
        foreach (var key in familyKeys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (!AssetCategoryExampleSeeds.ByFamilyKey.TryGetValue(key.Trim(), out var name))
            {
                continue;
            }

            if (!names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
            }
        }

        if (names.Count == 0)
        {
            return;
        }

        var existingNames = await dbContext.AssetCategories
            .IgnoreQueryFilters()
            .Where(category => category.TenantId == tenantId && names.Contains(category.Name))
            .Select(category => category.Name)
            .ToListAsync(cancellationToken);

        var skip = new HashSet<string>(existingNames, StringComparer.Ordinal);

        foreach (var tracked in dbContext.AssetCategories.Local)
        {
            if (tracked.TenantId == tenantId)
            {
                skip.Add(tracked.Name);
            }
        }

        foreach (var name in names)
        {
            if (!skip.Add(name))
            {
                continue;
            }

            dbContext.AssetCategories.Add(new AssetCategory
            {
                TenantId = tenantId,
                Name = name,
                Description = null,
                Manufacturer = null,
            });
        }
    }
}
