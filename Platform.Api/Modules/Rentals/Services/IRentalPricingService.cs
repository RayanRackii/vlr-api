using Platform.Api.Modules.Rentals.Dtos;

namespace Platform.Api.Modules.Rentals.Services;

public interface IRentalPricingService
{
    Task<IReadOnlyList<RentalPricingResponseDto>> GetByAssetIdAsync(
        Guid assetId,
        CancellationToken cancellationToken);

    Task<RentalPricingResponseDto> CreateAsync(
        Guid assetId,
        CreateRentalPricingDto request,
        CancellationToken cancellationToken);

    Task<RentalPricingResponseDto?> UpdateAsync(
        Guid assetId,
        Guid pricingId,
        UpdateRentalPricingDto request,
        CancellationToken cancellationToken);

    Task<bool> DeleteAsync(
        Guid assetId,
        Guid pricingId,
        CancellationToken cancellationToken);
}
