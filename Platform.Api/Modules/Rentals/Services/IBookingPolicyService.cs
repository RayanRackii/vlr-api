using Platform.Api.Modules.Rentals.Dtos;

namespace Platform.Api.Modules.Rentals.Services;

public interface IBookingPolicyService
{
    Task<IReadOnlyList<CategoryBookingPolicyResponseDto>> GetCategoriesAsync(CancellationToken cancellationToken);

    Task<CategoryBookingPolicyResponseDto> UpsertCategoryAsync(
        Guid categoryId, UpsertCategoryBookingPolicyRequestDto request, CancellationToken cancellationToken);

    Task<BookingPolicyResponseDto> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Replaces cancellation text and the daily cap. Omitted or null clears those.
    /// An omitted horizon keeps the stored value; explicit null clears it.
    /// </summary>
    Task<BookingPolicyResponseDto> UpsertAsync(
        UpsertBookingPolicyRequestDto request,
        CancellationToken cancellationToken);
}
