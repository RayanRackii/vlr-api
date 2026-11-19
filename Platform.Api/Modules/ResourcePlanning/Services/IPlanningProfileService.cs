using Platform.Api.Modules.ResourcePlanning.Dtos;

namespace Platform.Api.Modules.ResourcePlanning.Services;

public interface IPlanningProfileService
{
    /// <param name="search">Case-insensitive match on asset name or tag.</param>
    /// <param name="unitId">Restrict to one unit.</param>
    /// <param name="plannable"><c>true</c>, <c>false</c> or <c>all</c> (default).</param>
    Task<IReadOnlyList<PlanningAssetResponse>> ListAssetsAsync(
        string? search,
        Guid? unitId,
        string? plannable,
        CancellationToken cancellationToken);

    /// <summary>Null when the asset does not exist in the current tenant.</summary>
    Task<AssetPlanningProfileResponse?> GetProfileAsync(
        Guid assetId,
        CancellationToken cancellationToken);

    /// <summary>Upserts the profile and replaces the weekly hours atomically.</summary>
    Task<AssetPlanningProfileResponse> UpsertProfileAsync(
        Guid assetId,
        PutAssetPlanningProfileRequest request,
        CancellationToken cancellationToken);
}
