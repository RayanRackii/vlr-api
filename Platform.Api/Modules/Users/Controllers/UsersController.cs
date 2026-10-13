using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Common;
using Platform.Api.Modules.Users.Dtos;
using Platform.Api.Modules.Users.Services;
using Platform.Core.Domain.Constants;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Users.Controllers;

[ApiController]
[Authorize]
[Route("api/users")]
public sealed class UsersController(
    IUserDirectoryService userDirectoryService,
    IRbacActorAccessor rbacActorAccessor,
    ITenantProvider tenantProvider,
    IAuthorizationService authorizationService) : ControllerBase
{
    [HttpGet("me")]
    public async Task<ActionResult<CurrentUserResponse>> GetCurrent(
        CancellationToken cancellationToken)
    {
        try
        {
            var user = await userDirectoryService.GetCurrentAsync(User, cancellationToken);
            return Ok(user);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? q,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DirectoryPage.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var denied = await DirectoryAuthorization.GateAsync(
            this,
            tenantProvider,
            authorizationService,
            User,
            Permissions.Core.UsersRead);
        if (denied is not null)
        {
            return denied;
        }

        if (!DirectoryPage.TryCreate(q, page, pageSize, out var directoryPage, out var error))
        {
            return BadRequest(new { error });
        }

        var users = await userDirectoryService.ListAsync(directoryPage, cancellationToken);
        return Ok(users);
    }

    [HttpGet("technicians")]
    [RequirePermission(Permissions.Core.UsersRead)]
    public async Task<ActionResult<IReadOnlyList<TechnicianUserResponse>>> ListTechnicians(
        CancellationToken cancellationToken)
    {
        var technicians = await userDirectoryService.ListTechniciansAsync(cancellationToken);
        return Ok(technicians);
    }

    [HttpPut("{userId:guid}/roles")]
    [RequirePermission(Permissions.Core.UsersAssignRoles)]
    public async Task<IActionResult> AssignRoles(
        Guid userId,
        [FromBody] AssignUserRolesRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var actor = await rbacActorAccessor.GetAsync(User, cancellationToken);
            await userDirectoryService.AssignRolesAsync(
                userId,
                actor,
                request.RoleIds,
                cancellationToken);
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
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("invite")]
    [RequirePermission(Permissions.Core.UsersInvite)]
    public async Task<ActionResult<InviteTenantMemberResponse>> Invite(
        [FromBody] InviteTenantMemberRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var actor = await rbacActorAccessor.GetAsync(User, cancellationToken);
            var invite = await userDirectoryService.InviteAsync(actor, request, cancellationToken);
            return Created("/api/users", invite);
        }
        catch (RbacException ex)
        {
            return StatusCode(ex.HttpStatus, new { error = ex.Code });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }
}
