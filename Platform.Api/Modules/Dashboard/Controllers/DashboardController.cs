using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.Dashboard.Dtos;
using Platform.Api.Modules.Dashboard.Services;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.Dashboard.Controllers;

[ApiController]
[Route("api/dashboard")]
public sealed class DashboardController(
    IDashboardService dashboardService) : ControllerBase
{
    [HttpGet("metrics")]
    [RequirePermission(Permissions.Core.DashboardRead)]
    public async Task<ActionResult<DashboardMetricsDto>> GetMetrics(
        CancellationToken cancellationToken)
    {
        var metrics = await dashboardService.GetMetricsAsync(cancellationToken);
        return Ok(metrics);
    }
}
