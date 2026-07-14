using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Assets.Services;

public sealed class AssetService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider) : IAssetService
{
    private const int MaxBulkCreateCount = 1000;

    public async Task<IReadOnlyList<AssetResponse>> ListAsync(
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var assets = await dbContext.Assets
            .AsNoTracking()
            .OrderBy(a => a.Tag)
            .ToListAsync(cancellationToken);

        return assets.Select(ToResponse).ToList();
    }

    public async Task<AssetResponse?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var asset = await dbContext.Assets
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

        return asset is null ? null : ToResponse(asset);
    }

    public async Task<AssetResponse> CreateAsync(
        CreateAssetRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();

        await EnsureUnitExistsAsync(request.UnitId, cancellationToken);
        await EnsureCategoryExistsAsync(request.CategoryId, cancellationToken);

        var asset = new Asset
        {
            TenantId = tenantId,
            UnitId = request.UnitId,
            CategoryId = request.CategoryId,
            Name = request.Name.Trim(),
            Tag = request.Tag.Trim(),
            Location = NormalizeOptional(request.Location),
            SerialNumber = NormalizeOptional(request.SerialNumber),
            InstallationDate = request.InstallationDate,
            Status = request.Status,
        };

        dbContext.Assets.Add(asset);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToResponse(asset);
    }

    public async Task<AssetResponse?> UpdateAsync(
        Guid id,
        UpdateAssetRequest request,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var asset = await dbContext.Assets
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

        if (asset is null)
        {
            return null;
        }

        await EnsureUnitExistsAsync(request.UnitId, cancellationToken);
        await EnsureCategoryExistsAsync(request.CategoryId, cancellationToken);

        asset.UnitId = request.UnitId;
        asset.CategoryId = request.CategoryId;
        asset.Name = request.Name.Trim();
        asset.Tag = request.Tag.Trim();
        asset.Location = NormalizeOptional(request.Location);
        asset.SerialNumber = NormalizeOptional(request.SerialNumber);
        asset.InstallationDate = request.InstallationDate;
        asset.Status = request.Status;
        asset.Touch();

        await dbContext.SaveChangesAsync(cancellationToken);

        return ToResponse(asset);
    }

    public async Task<DeleteAssetResult?> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var asset = await dbContext.Assets
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

        if (asset is null)
        {
            return null;
        }

        if (asset.ScheduledDeletionAt is null)
        {
            asset.ScheduledDeletionAt = DateTimeOffset.UtcNow.AddDays(30);
            asset.Touch();
            await dbContext.SaveChangesAsync(cancellationToken);

            return new DeleteAssetResult(
                PermanentlyDeleted: false,
                Asset: ToResponse(asset));
        }

        dbContext.Assets.Remove(asset);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new DeleteAssetResult(
            PermanentlyDeleted: true,
            Asset: null);
    }

    public async Task<BulkCreateAssetsResponse> BulkCreateAsync(
        BulkCreateAssetsRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();

        if (request.StartNumber > request.EndNumber)
        {
            throw new ArgumentException("StartNumber must be less than or equal to EndNumber.");
        }

        var createCount = request.EndNumber - request.StartNumber + 1;

        if (createCount > MaxBulkCreateCount)
        {
            throw new ArgumentException(
                $"Bulk create is limited to {MaxBulkCreateCount} assets per request.");
        }

        await EnsureUnitExistsAsync(request.UnitId, cancellationToken);
        var category = await EnsureCategoryExistsAsync(request.CategoryId, cancellationToken);

        var baseTag = request.BaseTag.Trim();
        var baseLocation = request.BaseLocationName.Trim();
        var generatedTags = new List<string>(createCount);

        for (var number = request.StartNumber; number <= request.EndNumber; number++)
        {
            generatedTags.Add(BuildTag(baseTag, number));
        }

        var existingTags = await dbContext.Assets
            .AsNoTracking()
            .Where(a => generatedTags.Contains(a.Tag))
            .Select(a => a.Tag)
            .ToListAsync(cancellationToken);

        if (existingTags.Count > 0)
        {
            throw new InvalidOperationException(
                $"One or more tags already exist: {string.Join(", ", existingTags)}");
        }

        var assets = new List<Asset>(createCount);

        for (var number = request.StartNumber; number <= request.EndNumber; number++)
        {
            var tag = BuildTag(baseTag, number);

            assets.Add(new Asset
            {
                TenantId = tenantId,
                UnitId = request.UnitId,
                CategoryId = category.Id,
                Name = $"{category.Name} {tag}",
                Tag = tag,
                Location = $"{baseLocation} {number}",
                Status = AssetStatus.Active,
            });
        }

        await dbContext.Assets.AddRangeAsync(assets, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        var responses = assets
            .OrderBy(a => a.Tag)
            .Select(ToResponse)
            .ToList();

        return new BulkCreateAssetsResponse(responses.Count, responses);
    }

    private async Task EnsureUnitExistsAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var exists = await dbContext.Units
            .AsNoTracking()
            .AnyAsync(u => u.Id == unitId, cancellationToken);

        if (!exists)
        {
            throw new KeyNotFoundException($"Unit '{unitId}' was not found.");
        }
    }

    private async Task<AssetCategory> EnsureCategoryExistsAsync(
        Guid categoryId,
        CancellationToken cancellationToken)
    {
        var category = await dbContext.AssetCategories
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == categoryId, cancellationToken);

        if (category is null)
        {
            throw new KeyNotFoundException($"Asset category '{categoryId}' was not found.");
        }

        return category;
    }

    private Guid EnsureTenantContext()
    {
        return tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");
    }

    private static string BuildTag(string baseTag, int number)
    {
        return baseTag.EndsWith('-') || baseTag.EndsWith('_')
            ? $"{baseTag}{number}"
            : $"{baseTag}-{number}";
    }

    private static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static AssetResponse ToResponse(Asset asset) =>
        new(
            asset.Id,
            asset.TenantId,
            asset.UnitId,
            asset.CategoryId,
            asset.Name,
            asset.Tag,
            asset.Location,
            asset.SerialNumber,
            asset.InstallationDate,
            asset.Status,
            asset.CreatedAt,
            asset.UpdatedAt,
            asset.ScheduledDeletionAt);
}
