using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Serialization;

namespace Platform.Api.Modules.Rentals.Services;

public interface IRentalAssetService
{
    Task<IReadOnlyList<RentalAssetResponse>> ListRentableAsync(CancellationToken cancellationToken);

    Task<RentalAssetResponse?> GetByAssetIdAsync(Guid assetId, CancellationToken cancellationToken);

    Task<RentalAssetResponse> UpdateSchedulePolicyAsync(
        Guid rentalAssetId,
        UpdateRentalSchedulePolicyRequestDto request,
        CancellationToken cancellationToken);

    Task<BulkUpdateRentalSchedulePolicyResponseDto> UpdateSchedulePolicyBulkAsync(
        BulkUpdateRentalSchedulePolicyRequestDto request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes the per-resource horizon when <paramref name="maxAdvanceBookingDays"/>
    /// was sent. An unspecified value keeps the stored override.
    /// </summary>
    Task<RentalAssetResponse> SetMaxAdvanceBookingDaysAsync(
        Guid rentalAssetId,
        JsonOptional<int> maxAdvanceBookingDays,
        CancellationToken cancellationToken);
}
