using Platform.Api.Modules.Pmoc.Dtos;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Pmoc.Services;

public interface IMaintenancePlanCoverageService
{
    Task<MaintenancePlanCoverageResponse?> GetCoverageAsync(
        Guid planId,
        IReadOnlyCollection<PmocOperationalStatus>? statuses,
        CancellationToken cancellationToken);
}
