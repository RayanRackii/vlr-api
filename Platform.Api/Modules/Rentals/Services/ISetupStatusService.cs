using Platform.Api.Modules.Rentals.Dtos;

namespace Platform.Api.Modules.Rentals.Services;

public interface ISetupStatusService
{
    Task<SetupStatusResponse> GetAsync(CancellationToken cancellationToken);
}
