using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.Rentals.Controllers;

[ApiController]
[RequireActiveModule(PlatformModules.Rentals)]
[Route("api/rentals/setup-status")]
public sealed class SetupStatusController(ISetupStatusService setupStatusService) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Rentals.AssetsRead)]
    public async Task<ActionResult<SetupStatusResponse>> Get(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await setupStatusService.GetAsync(cancellationToken));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }
}
