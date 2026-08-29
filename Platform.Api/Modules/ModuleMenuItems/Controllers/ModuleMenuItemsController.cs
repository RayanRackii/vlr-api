using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.ModuleMenuItems.Dtos;
using Platform.Api.Modules.ModuleMenuItems.Services;
using Platform.Core.Domain.Constants;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.ModuleMenuItems.Controllers;

[ApiController]
[Route("api/module-menu-items")]
public sealed class ModuleMenuItemsController(
    IModuleMenuItemService moduleMenuItemService,
    ITenantProvider tenantProvider) : ControllerBase
{
    /// <summary>Public B2C sidebar menu for a tenant subdomain.</summary>
    [AllowAnonymous]
    [HttpGet("~/api/public/tenants/{subdomain}/menu")]
    public async Task<ActionResult<IReadOnlyList<ModuleMenuItemDto>>> GetPublicMenu(
        string subdomain,
        CancellationToken cancellationToken)
    {
        try
        {
            var items = await moduleMenuItemService.GetPublicMenuBySubdomainAsync(
                subdomain,
                cancellationToken);
            return Ok(items);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpGet]
    [RequirePermission(Permissions.Core.ModuleMenuRead)]
    public async Task<ActionResult<IReadOnlyList<ModuleMenuItemDto>>> List(
        CancellationToken cancellationToken)
    {
        var tenantId = tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");

        var items = await moduleMenuItemService.ListForTenantAsync(tenantId, cancellationToken);
        return Ok(items);
    }

    [HttpPost]
    [RequirePermission(Permissions.Core.ModuleMenuWrite)]
    public async Task<ActionResult<ModuleMenuItemDto>> Create(
        [FromBody] UpsertModuleMenuItemRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");

        try
        {
            var created = await moduleMenuItemService.CreateAsync(
                tenantId,
                request,
                cancellationToken);
            return CreatedAtAction(nameof(List), new { id = created.Id }, created);
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
    [RequirePermission(Permissions.Core.ModuleMenuWrite)]
    public async Task<ActionResult<ModuleMenuItemDto>> Update(
        Guid itemId,
        [FromBody] UpdateModuleMenuItemRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");

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
    [RequirePermission(Permissions.Core.ModuleMenuWrite)]
    public async Task<IActionResult> Delete(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var tenantId = tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");

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
