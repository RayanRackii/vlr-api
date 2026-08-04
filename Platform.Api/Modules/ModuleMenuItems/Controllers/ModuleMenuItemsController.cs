using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Modules.ModuleMenuItems.Dtos;
using Platform.Api.Modules.ModuleMenuItems.Services;
using Platform.Api.Modules.Users.Dtos;
using Platform.Api.Modules.Users.Services;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.ModuleMenuItems.Controllers;

[ApiController]
[Route("api/module-menu-items")]
[Authorize]
public sealed class ModuleMenuItemsController(
    IModuleMenuItemService moduleMenuItemService,
    IUserDirectoryService userDirectoryService,
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
    public async Task<ActionResult<IReadOnlyList<ModuleMenuItemDto>>> List(
        CancellationToken cancellationToken)
    {
        await EnsureTenantAdminAsync(cancellationToken);
        var tenantId = tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");

        var items = await moduleMenuItemService.ListForTenantAsync(tenantId, cancellationToken);
        return Ok(items);
    }

    [HttpPost]
    public async Task<ActionResult<ModuleMenuItemDto>> Create(
        [FromBody] UpsertModuleMenuItemRequestDto request,
        CancellationToken cancellationToken)
    {
        await EnsureTenantAdminAsync(cancellationToken);
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
    public async Task<ActionResult<ModuleMenuItemDto>> Update(
        Guid itemId,
        [FromBody] UpdateModuleMenuItemRequestDto request,
        CancellationToken cancellationToken)
    {
        await EnsureTenantAdminAsync(cancellationToken);
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
    public async Task<IActionResult> Delete(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        await EnsureTenantAdminAsync(cancellationToken);
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

    private async Task EnsureTenantAdminAsync(CancellationToken cancellationToken)
    {
        var current = await userDirectoryService.GetCurrentAsync(User, cancellationToken);
        if (current.Role is not (ApplicationRoles.Admin or ApplicationRoles.SuperAdmin))
        {
            throw new UnauthorizedAccessException(
                "Only tenant administrators can manage module menu items.");
        }
    }
}
