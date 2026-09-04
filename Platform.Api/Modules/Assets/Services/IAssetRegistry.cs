using Platform.Api.Modules.Assets.Dtos;
using Platform.Core.Domain.Entities;

namespace Platform.Api.Modules.Assets.Services;

/// <summary>
/// Tenant-scoped Asset Registry access for dependent modules (Rentals, PMOC, OS).
/// Does not check commercial module entitlements — controllers are the authz boundary.
/// </summary>
public interface IAssetRegistry
{
    Task<IReadOnlyList<RegistryCategoryListItem>> ListCategoriesAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RegistryAssetListItem>> ListAssetsAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AssetFamilyDetailResponse>> ListActiveFamiliesAsync(
        CancellationToken cancellationToken);

    Task<Asset> RequireAssetAsync(Guid id, CancellationToken cancellationToken);

    Task<AssetCategory> RequireCategoryAsync(Guid id, CancellationToken cancellationToken);

    Task<Asset> CreateRentableAsync(
        CreateRentableRequest request,
        CancellationToken cancellationToken);

    Task<Asset> UpdateRentableAsync(
        Guid rentalAssetId,
        UpdateRentableRequest request,
        CancellationToken cancellationToken);
}
