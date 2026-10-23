using Microsoft.EntityFrameworkCore;
using Npgsql;
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
        var name = NormalizeName(request.Name);

        if (await dbContext.AssetCategories.AnyAsync(c => c.Name == name, cancellationToken))
        {
            throw DuplicateName();
        }

        var category = new AssetCategory
        {
            TenantId = tenantId,
            Name = name,
            Manufacturer = NormalizeOptional(request.Manufacturer),
            Description = NormalizeOptional(request.Description),
        };

        dbContext.AssetCategories.Add(category);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsNameUniqueViolation(ex))
        {
            dbContext.ChangeTracker.Clear();
            throw DuplicateName();
        }

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

        var name = NormalizeName(request.Name);
        if (await dbContext.AssetCategories.AnyAsync(c => c.Id != id && c.Name == name, cancellationToken))
        {
            throw DuplicateName();
        }

        category.Name = name;
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
            throw new CategoryInUseException(
                "Cannot permanently delete an asset category that is referenced by maintenance plans.");
        }

        var assetIds = category.Assets.Select(asset => asset.Id).ToList();
        var hasWorkOrders = assetIds.Count > 0
            && await dbContext.WorkOrders.AnyAsync(
                workOrder => assetIds.Contains(workOrder.AssetId),
                cancellationToken);

        if (hasWorkOrders)
        {
            throw new CategoryInUseException(
                "Cannot permanently delete an asset category while its assets are referenced by work orders.");
        }

        dbContext.AssetCategories.Remove(category);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (AssetPersistenceConflicts.IsForeignKeyViolation(ex))
        {
            dbContext.ChangeTracker.Clear();
            throw new CategoryInUseException(
                "Cannot permanently delete an asset category that is referenced by maintenance plans or work orders.");
        }

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

    private static string NormalizeName(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            throw new ArgumentException("Asset category name is required.", nameof(value));
        }

        if (normalized.Length > 200)
        {
            throw new ArgumentException("Asset category name must be at most 200 characters.", nameof(value));
        }

        return normalized;
    }

    private static InvalidOperationException DuplicateName() =>
        new("An asset category with this name already exists.");

    private static bool IsNameUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "ix_asset_categories_tenant_id_name",
        };

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
