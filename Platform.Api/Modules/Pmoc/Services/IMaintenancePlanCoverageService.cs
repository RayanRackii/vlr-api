using Platform.Api.Modules.Pmoc.Dtos;

namespace Platform.Api.Modules.Pmoc.Services;

public interface IMaintenancePlanCoverageService
{
    Task<MaintenancePlanCoverageResponse?> GetCoverageAsync(
        Guid planId,
        CancellationToken cancellationToken);
}
