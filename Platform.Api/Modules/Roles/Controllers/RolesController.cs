using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.Roles.Dtos;
using Platform.Api.Modules.Roles.Services;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.Roles.Controllers;

[ApiController]
[Route("api/roles")]
public sealed class RolesController(
    IRoleService roleService,
    IRbacActorAccessor rbacActorAccessor) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Core.RolesRead)]
    public async Task<ActionResult<IReadOnlyList<RoleResponse>>> List(
        CancellationToken cancellationToken)
    {
        var actor = await rbacActorAccessor.GetAsync(User, cancellationToken);
        return Ok(await roleService.ListAsync(actor.TenantId, cancellationToken));
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Core.RolesRead)]
    public async Task<ActionResult<RoleResponse>> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        var actor = await rbacActorAccessor.GetAsync(User, cancellationToken);
        var role = await roleService.GetByIdAsync(actor.TenantId, id, cancellationToken);
        return role is null ? NotFound(new { error = "Role was not found." }) : Ok(role);
    }

    [HttpPost]
    [RequirePermission(Permissions.Core.RolesManage)]
    public async Task<ActionResult<RoleResponse>> Create(
        [FromBody] CreateRoleRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var actor = await rbacActorAccessor.GetAsync(User, cancellationToken);
            var created = await roleService.CreateAsync(
                actor.TenantId,
                actor,
                request,
                cancellationToken);
            return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
        }
        catch (RbacException ex)
        {
            return StatusCode(ex.HttpStatus, new { error = ex.Code });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPatch("{id:guid}")]
    [RequirePermission(Permissions.Core.RolesManage)]
    public async Task<ActionResult<RoleResponse>> Patch(
        Guid id,
        [FromBody] PatchRoleRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var actor = await rbacActorAccessor.GetAsync(User, cancellationToken);
            return Ok(await roleService.PatchAsync(actor.TenantId, actor, id, request, cancellationToken));
        }
        catch (RbacException ex)
        {
            return StatusCode(ex.HttpStatus, new { error = ex.Code });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("{id:guid}/permissions")]
    [RequirePermission(Permissions.Core.RolesManage)]
    public async Task<ActionResult<RoleResponse>> ReplacePermissions(
        Guid id,
        [FromBody] ReplaceRolePermissionsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var actor = await rbacActorAccessor.GetAsync(User, cancellationToken);
            return Ok(await roleService.ReplacePermissionsAsync(
                actor.TenantId,
                actor,
                id,
                request.PermissionKeys,
                cancellationToken));
        }
        catch (RbacException ex)
        {
            return StatusCode(ex.HttpStatus, new { error = ex.Code });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(Permissions.Core.RolesManage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var actor = await rbacActorAccessor.GetAsync(User, cancellationToken);
            await roleService.DeleteAsync(actor.TenantId, id, cancellationToken);
            return NoContent();
        }
        catch (RbacException ex)
        {
            return StatusCode(ex.HttpStatus, new { error = ex.Code });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }
}

[ApiController]
[Route("api/permissions")]
public sealed class PermissionsController : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Core.RolesRead)]
    public ActionResult<IReadOnlyList<PermissionCatalogItemResponse>> List()
    {
        var items = PermissionCatalog.All
            .Select(entry => new PermissionCatalogItemResponse(
                entry.Key,
                entry.Name,
                entry.Description,
                entry.ModuleKey,
                entry.Resource))
            .ToList();

        return Ok(items);
    }
}
