using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Jobs;

public static class PmocAssetEligibility
{
    public static bool Matches(Asset asset, Guid tenantId, Guid unitId, Guid categoryId) =>
        asset.TenantId == tenantId
        && asset.UnitId == unitId
        && asset.CategoryId == categoryId
        && asset.Status == AssetStatus.Active
        && asset.ScheduledDeletionAt == null;

    public static IQueryable<Asset> WhereEligible(
        IQueryable<Asset> assets,
        Guid tenantId,
        Guid unitId,
        Guid categoryId) =>
        assets.Where(asset =>
            asset.TenantId == tenantId
            && asset.UnitId == unitId
            && asset.CategoryId == categoryId
            && asset.Status == AssetStatus.Active
            && asset.ScheduledDeletionAt == null);
}
