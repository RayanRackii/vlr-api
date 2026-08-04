using Platform.Api.Modules.Rentals.Dtos;

namespace Platform.Api.Modules.Rentals.Services;

public interface IOccupancyKindService
{
    Task<IReadOnlyList<OccupancyKindResponseDto>> ListAsync(CancellationToken cancellationToken);

    Task<OccupancyKindResponseDto> CreateAsync(
        UpsertOccupancyKindRequestDto request,
        CancellationToken cancellationToken);

    Task<OccupancyKindResponseDto> UpdateAsync(
        Guid id,
        UpsertOccupancyKindRequestDto request,
        CancellationToken cancellationToken);

    Task EnsureDefaultsAsync(CancellationToken cancellationToken);
}
