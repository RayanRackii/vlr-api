using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Modules.Pmoc.Dtos;
using Platform.Api.Modules.Pmoc.Services;

namespace Platform.Api.Modules.Pmoc.Controllers;

[ApiController]
[Authorize]
[Route("api/global-templates")]
public sealed class GlobalTemplatesController(
    IGlobalTemplateService globalTemplateService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<GlobalMaintenanceTemplateResponse>>> List(
        [FromQuery] string? jurisdiction,
        CancellationToken cancellationToken)
    {
        var templates = await globalTemplateService.ListAsync(
            jurisdiction,
            cancellationToken);

        return Ok(templates);
    }
}
