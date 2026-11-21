using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authentication;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Modules.Admin.Services;

namespace Platform.Api.Modules.Admin.Controllers;

/// <summary>
/// Platform-admin view of the data a disabled module still holds for a tenant (read-only preview;
/// scheduling and execution arrive in a later slice).
/// </summary>
[ApiController]
[Authorize(Policy = SupabaseAuthenticationExtensions.PlatformAdminPolicy)]
[Route("api/admin/tenants/{id:guid}/modules/{module}/data-purge")]
public sealed class AdminModuleDataPurgeController(IModuleDataPurgeService dataPurge) : ControllerBase
{
    [HttpGet("preview")]
    public async Task<ActionResult<ModuleDataPurgePreviewDto>> Preview(
        Guid id,
        string module,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await dataPurge.PreviewAsync(id, module, cancellationToken));
        }
        catch (ModuleDataPurgeRequestException ex)
        {
            return BadRequest(new { error = ex.Message, code = ex.Code });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }
}
