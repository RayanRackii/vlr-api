using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.Rentals.Controllers;

[ApiController]
[RequireActiveModule(PlatformModules.Rentals)]
[Route("api/rentals/readiness")]
public sealed class RentalsReadinessController(IRentalsReadinessService readinessService) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Rentals.AssetsRead)]
    public async Task<ActionResult<RentalsReadinessResponse>> Get(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await readinessService.GetAsync(cancellationToken));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }
}
