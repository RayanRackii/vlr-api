using Platform.Api.Modules.Dashboard.Dtos;

namespace Platform.Api.Modules.Dashboard.Services;

public interface IDashboardService
{
    Task<DashboardMetricsDto> GetMetricsAsync(CancellationToken cancellationToken);
}
