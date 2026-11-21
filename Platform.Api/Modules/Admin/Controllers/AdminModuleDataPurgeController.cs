using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authentication;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Modules.Admin.Services;

namespace Platform.Api.Modules.Admin.Controllers;

/// <summary>
/// Platform-admin endpoints for the data a disabled module still holds for a tenant: preview,
/// schedule (30 days ahead, typed confirmation) and cancel. The daily job executes what is scheduled.
/// Tenant admins never reach these routes.
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

    /// <summary>
    /// Schedules the purge 30 days ahead. 409 with the preview body when blockers exist, 409 with
    /// <c>purge.already_pending</c> when one is already scheduled.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ModuleDataPurgeScheduledDto>> Schedule(
        Guid id,
        string module,
        [FromBody] ScheduleModuleDataPurgeRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var scheduled = await dataPurge.ScheduleAsync(id, module, request.Confirm, Actor(), cancellationToken);
            return Created($"/api/admin/tenants/{id}/modules/{module}/data-purge/preview", scheduled);
        }
        catch (ModuleDataPurgeRequestException ex)
        {
            return BadRequest(new { error = ex.Message, code = ex.Code });
        }
        catch (ModuleDataPurgeBlockedException ex)
        {
            return Conflict(ex.Preview);
        }
        catch (ModuleDataPurgeConflictException ex)
        {
            return Conflict(new { error = ex.Message, code = ex.Code });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>Cancels the pending purge. The reason is optional and kept on the purge row with who and when.</summary>
    [HttpDelete]
    public async Task<IActionResult> Cancel(
        Guid id,
        string module,
        [FromQuery] string? reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await dataPurge.CancelAsync(id, module, reason, Actor(), cancellationToken);
            return NoContent();
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

    private string? Actor() =>
        User.FindFirstValue("sub")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.FindFirstValue("email");
}
