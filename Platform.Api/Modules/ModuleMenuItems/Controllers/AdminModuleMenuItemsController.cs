using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authentication;
using Platform.Api.Modules.ModuleMenuItems.Dtos;
using Platform.Api.Modules.ModuleMenuItems.Services;

namespace Platform.Api.Modules.ModuleMenuItems.Controllers;

[ApiController]
[Route("api/admin/tenants/{tenantId:guid}/module-menu-items")]
[Authorize(Policy = SupabaseAuthenticationExtensions.PlatformAdminPolicy)]
public sealed class AdminModuleMenuItemsController(
    IModuleMenuItemService moduleMenuItemService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ModuleMenuItemDto>>> List(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        try
        {
            var items = await moduleMenuItemService.ListForTenantAsync(
                tenantId,
                cancellationToken);
            return Ok(items);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost]
    public async Task<ActionResult<ModuleMenuItemDto>> Create(
        Guid tenantId,
        [FromBody] UpsertModuleMenuItemRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await moduleMenuItemService.CreateAsync(
                tenantId,
                request,
                cancellationToken);
            return CreatedAtAction(nameof(List), new { tenantId }, created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPut("{itemId:guid}")]
    public async Task<ActionResult<ModuleMenuItemDto>> Update(
        Guid tenantId,
        Guid itemId,
        [FromBody] UpdateModuleMenuItemRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var updated = await moduleMenuItemService.UpdateAsync(
                tenantId,
                itemId,
                request,
                cancellationToken);
            return Ok(updated);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpDelete("{itemId:guid}")]
    public async Task<IActionResult> Delete(
        Guid tenantId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        try
        {
            await moduleMenuItemService.DeleteAsync(tenantId, itemId, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }
}
