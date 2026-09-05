using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.Pmoc.Dtos;
using Platform.Api.Modules.Pmoc.Services;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.Pmoc.Controllers;

[ApiController]
[RequireActiveModule(PlatformModules.Pmoc)]
[Route("api/global-templates")]
public sealed class GlobalTemplatesController(
    IGlobalTemplateService globalTemplateService) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Pmoc.TemplatesRead)]
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
