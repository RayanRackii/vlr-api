using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Jobs;

public static class PmocAssetEligibility
{
    public static bool Matches(Asset asset, Guid tenantId, Guid unitId, Guid? categoryId, Guid? assetId = null) =>
        asset.TenantId == tenantId
        && asset.UnitId == unitId
        && asset.Status == AssetStatus.Active
        && asset.ScheduledDeletionAt == null
        && (assetId is Guid specific
            ? asset.Id == specific
            : categoryId is Guid category && asset.CategoryId == category);

    public static IQueryable<Asset> WhereEligible(
        IQueryable<Asset> assets,
        Guid tenantId,
        Guid unitId,
        Guid? categoryId,
        Guid? assetId = null) =>
        assetId is Guid specific
            ? assets.Where(asset =>
                asset.TenantId == tenantId
                && asset.UnitId == unitId
                && asset.Id == specific
                && asset.Status == AssetStatus.Active
                && asset.ScheduledDeletionAt == null)
            : assets.Where(asset =>
                asset.TenantId == tenantId
                && asset.UnitId == unitId
                && categoryId != null
                && asset.CategoryId == categoryId
                && asset.Status == AssetStatus.Active
                && asset.ScheduledDeletionAt == null);
}
