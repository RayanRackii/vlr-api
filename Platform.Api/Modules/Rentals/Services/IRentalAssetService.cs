using Platform.Api.Modules.Rentals.Dtos;

namespace Platform.Api.Modules.Rentals.Services;

public interface IRentalAssetService
{
    Task<IReadOnlyList<RentalAssetResponse>> ListRentableAsync(CancellationToken cancellationToken);

    Task<RentalAssetResponse?> GetByAssetIdAsync(Guid assetId, CancellationToken cancellationToken);
}
