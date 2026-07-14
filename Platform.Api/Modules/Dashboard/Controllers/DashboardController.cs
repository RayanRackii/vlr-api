using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Modules.Dashboard.Dtos;
using Platform.Api.Modules.Dashboard.Services;

namespace Platform.Api.Modules.Dashboard.Controllers;

[ApiController]
[Authorize]
[Route("api/dashboard")]
public sealed class DashboardController(
    IDashboardService dashboardService) : ControllerBase
{
    [HttpGet("metrics")]
    public async Task<ActionResult<DashboardMetricsDto>> GetMetrics(
        CancellationToken cancellationToken)
    {
        var metrics = await dashboardService.GetMetricsAsync(cancellationToken);
        return Ok(metrics);
    }
}
