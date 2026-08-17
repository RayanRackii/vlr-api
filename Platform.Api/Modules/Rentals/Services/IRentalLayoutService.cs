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

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}
