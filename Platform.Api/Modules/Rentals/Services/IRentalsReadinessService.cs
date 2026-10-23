using Platform.Api.Modules.Rentals.Dtos;

namespace Platform.Api.Modules.Rentals.Services;

public interface IRentalsReadinessService
{
    Task<RentalsReadinessResponse> GetAsync(CancellationToken cancellationToken);
}
