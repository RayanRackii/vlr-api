using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Services.Trial;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Assets.Services;

public sealed class AssetService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    ITrialGuard trialGuard) : IAssetService
{
    private const int MaxBulkCreateCount = 1000;

    public async Task<IReadOnlyList<AssetResponse>> ListAsync(
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var assets = await dbContext.Assets
            .AsNoTracking()
            .Include(a => a.RentalConfiguration)
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
            .Include(a => a.RentalConfiguration)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

        return asset is null ? null : ToResponse(asset);
    }

    public async Task<AssetResponse> CreateAsync(
        CreateAssetRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();
        await trialGuard.EnsureCanCreateAssetsAsync(1, cancellationToken);

        ValidateRentalFields(request.IsRentable, request.RentalType, request.TotalQuantity);
        await EnsureUnitExistsAsync(request.UnitId, cancellationToken);
        await EnsureCategoryExistsAsync(request.CategoryId, cancellationToken);
        var family = await EnsureFamilyEnabledForTenantAsync(
            tenantId,
            request.FamilyId,
            cancellationToken);
        var attributes = AssetFamilyAttributeValidator.ValidateAndProject(
            family.FieldSchemaJson,
            request.Attributes);

        var asset = new Asset
        {
            TenantId = tenantId,
            UnitId = request.UnitId,
            CategoryId = request.CategoryId,
            FamilyId = family.Id,
            Name = request.Name.Trim(),
            Tag = request.Tag.Trim(),
            Location = NormalizeOptional(request.Location),
            SerialNumber = NormalizeOptional(request.SerialNumber),
            InstallationDate = request.InstallationDate,
            Status = request.Status,
            IsRentable = request.IsRentable,
            RequiresMaintenance = request.RequiresMaintenance,
            Attributes = attributes,
        };

        dbContext.Assets.Add(asset);
        SyncRentalConfiguration(
            asset,
            request.IsRentable,
            request.RentalType,
            request.TotalQuantity,
            request.RequiresDeposit);

        await dbContext.SaveChangesAsync(cancellationToken);

        return ToResponse(asset);
    }

    public async Task<AssetResponse?> UpdateAsync(
        Guid id,
        UpdateAssetRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();
        await trialGuard.EnsureWritableAsync(cancellationToken);

        ValidateRentalFields(request.IsRentable, request.RentalType, request.TotalQuantity);

        var asset = await dbContext.Assets
            .Include(a => a.RentalConfiguration)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

        if (asset is null)
        {
            return null;
        }

        await EnsureUnitExistsAsync(request.UnitId, cancellationToken);
        await EnsureCategoryExistsAsync(request.CategoryId, cancellationToken);
        var family = await EnsureFamilyEnabledForTenantAsync(
            tenantId,
            request.FamilyId,
            cancellationToken);
        var attributes = AssetFamilyAttributeValidator.ValidateAndProject(
            family.FieldSchemaJson,
            request.Attributes);

        asset.UnitId = request.UnitId;
        asset.CategoryId = request.CategoryId;
        asset.FamilyId = family.Id;
        asset.Name = request.Name.Trim();
        asset.Tag = request.Tag.Trim();
        asset.Location = NormalizeOptional(request.Location);
        asset.SerialNumber = NormalizeOptional(request.SerialNumber);
        asset.InstallationDate = request.InstallationDate;
        asset.Status = request.Status;
        asset.IsRentable = request.IsRentable;
        asset.RequiresMaintenance = request.RequiresMaintenance;
        asset.Attributes = attributes;
        asset.Touch();

        SyncRentalConfiguration(
            asset,
            request.IsRentable,
            request.RentalType,
            request.TotalQuantity,
            request.RequiresDeposit);

        await dbContext.SaveChangesAsync(cancellationToken);

        return ToResponse(asset);
    }

    public async Task<DeleteAssetResult?> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        EnsureTenantContext();
        await trialGuard.EnsureWritableAsync(cancellationToken);

        var asset = await dbContext.Assets
            .Include(a => a.RentalConfiguration)
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
        var plan = ResolveBulkCreatePlan(request);
        ValidateRentalFields(request.IsRentable, plan.RentalType, plan.TotalQuantity);

        if (plan.Tags.Count > MaxBulkCreateCount)
        {
            throw new ArgumentException(
                $"Bulk create is limited to {MaxBulkCreateCount} assets per request.");
        }

        await trialGuard.EnsureCanCreateAssetsAsync(plan.Tags.Count, cancellationToken);

        await EnsureUnitExistsAsync(request.UnitId, cancellationToken);
        var category = await EnsureCategoryExistsAsync(request.CategoryId, cancellationToken);
        var family = await EnsureFamilyEnabledForTenantAsync(
            tenantId,
            request.FamilyId,
            cancellationToken);
        var attributes = AssetFamilyAttributeValidator.ValidateAndProject(
            family.FieldSchemaJson,
            request.Attributes);

        var baseLocation = request.BaseLocationName.Trim();

        var existingTags = await dbContext.Assets
            .AsNoTracking()
            .Where(a => plan.Tags.Contains(a.Tag))
            .Select(a => a.Tag)
            .ToListAsync(cancellationToken);

        if (existingTags.Count > 0)
        {
            throw new InvalidOperationException(
                $"One or more tags already exist: {string.Join(", ", existingTags)}");
        }

        var assets = new List<Asset>(plan.Tags.Count);

        foreach (var tag in plan.Tags)
        {
            var asset = new Asset
            {
                TenantId = tenantId,
                UnitId = request.UnitId,
                CategoryId = category.Id,
                FamilyId = family.Id,
                Name = $"{category.Name} {tag}",
                Tag = tag,
                Location = string.IsNullOrWhiteSpace(baseLocation) ? null : baseLocation,
                Status = AssetStatus.Active,
                IsRentable = request.IsRentable,
                RequiresMaintenance = request.RequiresMaintenance,
                Attributes = new Dictionary<string, string?>(attributes, StringComparer.Ordinal),
            };

            SyncRentalConfiguration(
                asset,
                request.IsRentable,
                plan.RentalType,
                plan.TotalQuantity,
                request.RequiresDeposit);

            assets.Add(asset);
        }

        await dbContext.Assets.AddRangeAsync(assets, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        var responses = assets
            .OrderBy(a => a.Tag)
            .Select(ToResponse)
            .ToList();

        return new BulkCreateAssetsResponse(responses.Count, responses);
    }

    private void SyncRentalConfiguration(
        Asset asset,
        bool isRentable,
        RentalAssetType rentalType,
        int totalQuantity,
        bool requiresDeposit)
    {
        if (!isRentable)
        {
            if (asset.RentalConfiguration is not null)
            {
                asset.RentalConfiguration.IsActive = false;
                asset.RentalConfiguration.Touch();
            }

            return;
        }

        if (asset.RentalConfiguration is null)
        {
            asset.RentalConfiguration = new RentalAsset
            {
                TenantId = asset.TenantId,
                AssetId = asset.Id,
                Type = rentalType,
                TotalQuantity = totalQuantity,
                IsActive = true,
                RequiresDeposit = requiresDeposit,
                SchedulePolicy = SchedulePolicy.SlotGrid,
            };
            return;
        }

        asset.RentalConfiguration.Type = rentalType;
        asset.RentalConfiguration.TotalQuantity = totalQuantity;
        asset.RentalConfiguration.RequiresDeposit = requiresDeposit;
        asset.RentalConfiguration.IsActive = true;
        asset.RentalConfiguration.Touch();
    }

    private static void ValidateRentalFields(
        bool isRentable,
        RentalAssetType rentalType,
        int totalQuantity)
    {
        if (!isRentable)
        {
            return;
        }

        if (totalQuantity < 1)
        {
            throw new ArgumentException("TotalQuantity must be at least 1 when IsRentable is true.");
        }

        if (rentalType == RentalAssetType.Location && totalQuantity != 1)
        {
            throw new ArgumentException("Location rentals must have TotalQuantity equal to 1.");
        }
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

    private async Task<AssetFamily> EnsureFamilyEnabledForTenantAsync(
        Guid tenantId,
        Guid familyId,
        CancellationToken cancellationToken)
    {
        var family = await dbContext.AssetFamilies
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == familyId && f.IsActive, cancellationToken);

        if (family is null)
        {
            throw new KeyNotFoundException($"Asset family '{familyId}' was not found.");
        }

        var enabled = await dbContext.TenantAssetFamilies
            .AsNoTracking()
            .AnyAsync(t => t.TenantId == tenantId && t.FamilyId == familyId, cancellationToken);

        if (!enabled)
        {
            throw new ArgumentException(
                $"Asset family '{family.Key}' is not enabled for this tenant.");
        }

        return family;
    }

    private Guid EnsureTenantContext()
    {
        return tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");
    }

    private static BulkCreatePlan ResolveBulkCreatePlan(BulkCreateAssetsRequest request)
    {
        var baseTag = request.BaseTag.Trim();
        var rentalType = request.RentalType;

        if (rentalType == RentalAssetType.Location)
        {
            if (request.StartNumber is null || request.EndNumber is null)
            {
                throw new ArgumentException(
                    "StartNumber and EndNumber are required when RentalType is Location.");
            }

            var startNumber = request.StartNumber.Value;
            var endNumber = request.EndNumber.Value;

            if (startNumber > endNumber)
            {
                throw new ArgumentException("StartNumber must be less than or equal to EndNumber.");
            }

            var createCount = endNumber - startNumber + 1;
            var tags = new List<string>(createCount);
            for (var number = startNumber; number <= endNumber; number++)
            {
                tags.Add(BuildTag(baseTag, number));
            }

            return new BulkCreatePlan(rentalType, 1, tags);
        }

        if (rentalType == RentalAssetType.Good)
        {
            return new BulkCreatePlan(rentalType, request.TotalQuantity, [baseTag]);
        }

        throw new ArgumentException($"Unsupported RentalType '{rentalType}'.");
    }

    private static string BuildTag(string baseTag, int number)
    {
        return baseTag.EndsWith('-') || baseTag.EndsWith('_')
            ? $"{baseTag}{number}"
            : $"{baseTag}-{number}";
    }

    private sealed record BulkCreatePlan(
        RentalAssetType RentalType,
        int TotalQuantity,
        List<string> Tags);

    private static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static AssetResponse ToResponse(Asset asset)
    {
        AssetRentalConfigResponse? rentalConfig = null;

        if (asset.RentalConfiguration is not null)
        {
            rentalConfig = new AssetRentalConfigResponse(
                asset.RentalConfiguration.Id,
                asset.RentalConfiguration.Type,
                asset.RentalConfiguration.TotalQuantity,
                asset.RentalConfiguration.IsActive,
                asset.RentalConfiguration.RequiresDeposit);
        }

        return new(
            asset.Id,
            asset.TenantId,
            asset.UnitId,
            asset.CategoryId,
            asset.FamilyId,
            asset.Name,
            asset.Tag,
            asset.Location,
            asset.SerialNumber,
            asset.InstallationDate,
            asset.Status,
            asset.IsRentable,
            asset.RequiresMaintenance,
            asset.Attributes,
            rentalConfig,
            asset.CreatedAt,
            asset.UpdatedAt,
            asset.ScheduledDeletionAt);
    }
}
