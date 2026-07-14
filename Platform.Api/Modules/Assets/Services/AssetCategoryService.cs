using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Assets.Services;

public sealed class AssetCategoryService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider) : IAssetCategoryService
{
    private static readonly TimeSpan SoftDeleteRetention = TimeSpan.FromDays(30);

    public async Task<IReadOnlyList<AssetCategoryResponse>> ListAsync(
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var categories = await dbContext.AssetCategories
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new
            {
                Category = c,
                LinkedAssetsCount = c.Assets.Count(),
            })
            .ToListAsync(cancellationToken);

        return categories
            .Select(item => ToResponse(item.Category, item.LinkedAssetsCount))
            .ToList();
    }

    public async Task<AssetCategoryResponse?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var category = await dbContext.AssetCategories
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new
            {
                Category = c,
                LinkedAssetsCount = c.Assets.Count(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        return category is null
            ? null
            : ToResponse(category.Category, category.LinkedAssetsCount);
    }

    public async Task<AssetCategoryResponse> CreateAsync(
        CreateAssetCategoryRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();

        var category = new AssetCategory
        {
            TenantId = tenantId,
            Name = request.Name.Trim(),
            Manufacturer = NormalizeOptional(request.Manufacturer),
            Description = NormalizeOptional(request.Description),
        };

        dbContext.AssetCategories.Add(category);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToResponse(category, linkedAssetsCount: 0);
    }

    public async Task<AssetCategoryResponse?> UpdateAsync(
        Guid id,
        UpdateAssetCategoryRequest request,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var category = await dbContext.AssetCategories
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (category is null)
        {
            return null;
        }

        category.Name = request.Name.Trim();
        category.Manufacturer = NormalizeOptional(request.Manufacturer);
        category.Description = NormalizeOptional(request.Description);
        category.Touch();

        await dbContext.SaveChangesAsync(cancellationToken);

        var linkedAssetsCount = await dbContext.Assets
            .CountAsync(a => a.CategoryId == id, cancellationToken);

        return ToResponse(category, linkedAssetsCount);
    }

    public async Task<DeleteAssetCategoryResult?> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var category = await dbContext.AssetCategories
            .Include(c => c.Assets)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (category is null)
        {
            return null;
        }

        var affectedAssetsCount = category.Assets.Count;

        if (category.ScheduledDeletionAt is null)
        {
            var scheduledDeletionAt = DateTimeOffset.UtcNow.Add(SoftDeleteRetention);

            category.ScheduledDeletionAt = scheduledDeletionAt;
            category.Touch();

            foreach (var asset in category.Assets)
            {
                asset.ScheduledDeletionAt = scheduledDeletionAt;
                asset.Touch();
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            return new DeleteAssetCategoryResult(
                PermanentlyDeleted: false,
                AffectedAssetsCount: affectedAssetsCount,
                Category: ToResponse(category, affectedAssetsCount));
        }

        var hasMaintenancePlans = await dbContext.MaintenancePlans
            .AnyAsync(p => p.AssetCategoryId == id, cancellationToken);

        if (hasMaintenancePlans)
        {
            throw new InvalidOperationException(
                "Cannot permanently delete an asset category that is referenced by maintenance plans.");
        }

        dbContext.AssetCategories.Remove(category);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new DeleteAssetCategoryResult(
            PermanentlyDeleted: true,
            AffectedAssetsCount: affectedAssetsCount,
            Category: null);
    }

    private Guid EnsureTenantContext()
    {
        return tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");
    }

    private static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static AssetCategoryResponse ToResponse(
        AssetCategory category,
        int linkedAssetsCount) =>
        new(
            category.Id,
            category.TenantId,
            category.Name,
            category.Manufacturer,
            category.Description,
            category.CreatedAt,
            category.UpdatedAt,
            category.ScheduledDeletionAt,
            linkedAssetsCount);
}
