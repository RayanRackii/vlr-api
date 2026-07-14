using Platform.Api.Modules.Pmoc.Dtos;

namespace Platform.Api.Modules.Pmoc.Services;

public interface IMaintenancePlanService
{
    Task<IReadOnlyList<MaintenancePlanResponse>> ListAsync(CancellationToken cancellationToken);

    Task<MaintenancePlanResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<MaintenancePlanResponse> CreatePlanWithTasksAsync(
        CreateMaintenancePlanRequest request,
        CancellationToken cancellationToken);

    Task<MaintenancePlanResponse?> UpdateAsync(
        Guid id,
        UpdateMaintenancePlanRequest request,
        CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}
