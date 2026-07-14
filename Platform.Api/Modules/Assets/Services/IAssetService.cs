using Platform.Api.Modules.Assets.Dtos;

namespace Platform.Api.Modules.Assets.Services;

public interface IAssetService
{
    Task<IReadOnlyList<AssetResponse>> ListAsync(CancellationToken cancellationToken);

    Task<AssetResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<AssetResponse> CreateAsync(CreateAssetRequest request, CancellationToken cancellationToken);

    Task<AssetResponse?> UpdateAsync(
        Guid id,
        UpdateAssetRequest request,
        CancellationToken cancellationToken);

    Task<DeleteAssetResult?> DeleteAsync(Guid id, CancellationToken cancellationToken);

    Task<BulkCreateAssetsResponse> BulkCreateAsync(
        BulkCreateAssetsRequest request,
        CancellationToken cancellationToken);
}
