using Platform.Api.Modules.Assets.Dtos;

namespace Platform.Api.Modules.Assets.Services;

public interface IAssetCategoryService
{
    Task<IReadOnlyList<AssetCategoryResponse>> ListAsync(CancellationToken cancellationToken);

    Task<AssetCategoryResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<AssetCategoryResponse> CreateAsync(
        CreateAssetCategoryRequest request,
        CancellationToken cancellationToken);

    Task<AssetCategoryResponse?> UpdateAsync(
        Guid id,
        UpdateAssetCategoryRequest request,
        CancellationToken cancellationToken);

    Task<DeleteAssetCategoryResult?> DeleteAsync(Guid id, CancellationToken cancellationToken);
}
