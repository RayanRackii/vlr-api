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
    ITenantProvider tenantProvider) : ControllerBase
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

    /// <summary>
    /// Replaces the signed-in staff member's own phone. The route and body carry no user id.
    /// PUT is a staff write, so <c>StaffWriteSessionGuard</c> rejects a revoked or expired session
    /// the same way it does for every other staff write.
    /// </summary>
    [HttpPut("me")]
    public async Task<ActionResult<CurrentUserResponse>> UpdateCurrentPhone(
        [FromBody] UpdateCurrentUserPhoneRequest request,
        CancellationToken cancellationToken)
    {
        var missingTenant = DirectoryAuthorization.RequireTenant(this, tenantProvider);
        if (missingTenant is not null)
        {
            return missingTenant;
        }

        if (request.IsInvalid)
        {
            return BadRequest(new { error = "Phone must include DDD and number." });
        }

        if (!request.IsSpecified)
        {
            return BadRequest(new { error = "Phone is required." });
        }

        try
        {
            var user = await userDirectoryService.UpdateCurrentPhoneAsync(
                User,
                request.Phone,
                cancellationToken);
            return Ok(user);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }

    [HttpGet]
    [RequirePermission(Permissions.Core.UsersRead)]
    public async Task<IActionResult> List(
        [FromQuery] string? q,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DirectoryPage.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        if (!DirectoryPage.TryCreate(q, page, pageSize, out var directoryPage, out var error))
        {
            return BadRequest(new { error });
        }

        var users = await userDirectoryService.ListAsync(directoryPage, cancellationToken);
        return Ok(users);
    }

    /// <summary>
    /// Active users who can execute work orders. Callable with
    /// <c>core.users.read</c>, <c>os.work_orders.create</c>, or <c>os.work_orders.assign</c>.
    /// </summary>
    [HttpGet("technicians")]
    [RequireAnyPermission(
        Permissions.Core.UsersRead,
        Permissions.Os.WorkOrdersCreate,
        Permissions.Os.WorkOrdersAssign)]
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
