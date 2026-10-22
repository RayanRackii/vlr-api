using Platform.Api.Modules.Rentals.Dtos;

namespace Platform.Api.Modules.Rentals.Services;

public interface IRentalLayoutService
{
    Task<IReadOnlyList<RentalLayoutResponseDto>> ListAsync(CancellationToken cancellationToken);

    Task<RentalLayoutResponseDto?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<RentalLayoutResponseDto> CreateAsync(
        UpsertRentalLayoutRequestDto request,
        CancellationToken cancellationToken);

    Task<RentalLayoutResponseDto> UpdateAsync(
        Guid id,
        UpsertRentalLayoutRequestDto request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RentalLayoutResponseDto>> ListLegacyPublicAsync(CancellationToken cancellationToken);
    Task<BookingMapCompositionDto?> GetCompositionAsync(Guid id, CancellationToken cancellationToken);
    Task<BookingMapCompositionDto> SaveCompositionAsync(Guid id, SaveBookingMapCompositionRequest request, CancellationToken cancellationToken);
    Task<BookingMapSettingsDto> GetSettingsAsync(CancellationToken cancellationToken);
    Task<BookingMapSettingsDto> SaveSettingsAsync(SaveBookingMapSettingsRequest request, CancellationToken cancellationToken);
    Task<PublicBookingMapListDto> ListPublicMapsAsync(Guid? unitId, CancellationToken cancellationToken);
    Task<PublicBookingMapDto?> GetPublicMapAsync(Guid id, Guid? unitId, CancellationToken cancellationToken);
    Task<BookingMapCompositionDto> SeedDemoAsync(SeedDemoBookingMapRequest request, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}
