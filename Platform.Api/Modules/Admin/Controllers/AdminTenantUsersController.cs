using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authentication;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Modules.Admin.Services;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Modules.Admin.Controllers;

[ApiController]
[Authorize(Policy = SupabaseAuthenticationExtensions.PlatformAdminPolicy)]
[Route("api/admin/tenants/{tenantId:guid}")]
public sealed class AdminTenantUsersController(
    ITenantUserAdminService tenantUserAdminService) : ControllerBase
{
    [HttpGet("users")]
    public async Task<ActionResult<TenantUsersBundleDto>> ListUsers(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await tenantUserAdminService.ListUsersAndInvitesAsync(tenantId, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost("invites")]
    public async Task<ActionResult<TenantInviteResponseDto>> Invite(
        Guid tenantId,
        [FromBody] InviteTenantUserRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var invite = await tenantUserAdminService.InviteAsync(tenantId, request, cancellationToken);
            return Created($"/api/admin/tenants/{tenantId}/users", invite);
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

    [HttpPost("invites/{inviteId:guid}/resend")]
    public async Task<IActionResult> ResendInvite(
        Guid tenantId,
        Guid inviteId,
        CancellationToken cancellationToken)
    {
        try
        {
            await tenantUserAdminService.ResendInviteAsync(tenantId, inviteId, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("invites/{inviteId:guid}/revoke")]
    public async Task<IActionResult> RevokeInvite(
        Guid tenantId,
        Guid inviteId,
        CancellationToken cancellationToken)
    {
        try
        {
            await tenantUserAdminService.RevokeInviteAsync(tenantId, inviteId, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("users/{userId:guid}/roles")]
    public async Task<IActionResult> Promote(
        Guid tenantId,
        Guid userId,
        [FromBody] PromoteTenantUserRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            await tenantUserAdminService.PromoteAsync(tenantId, userId, request, cancellationToken);
            return NoContent();
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
}

[ApiController]
[Route("api/invites")]
public sealed class InvitesController(ITenantUserAdminService tenantUserAdminService) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("preview")]
    public async Task<ActionResult<InvitePreviewResponseDto>> Preview(
        [FromBody] InvitePreviewRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await tenantUserAdminService.PreviewInviteAsync(
                request.Token,
                cancellationToken);
            return Ok(result);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException or InvalidOperationException or InviteAcceptanceException or SupabaseAuthAdminException)
        {
            return InviteError(ex);
        }
    }

    [AllowAnonymous]
    [HttpPost("accept")]
    public async Task<ActionResult<AcceptInviteResponseDto>> Accept(
        [FromBody] AcceptInviteRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var actor = User?.Identity?.IsAuthenticated == true ? User : null;
            var result = await tenantUserAdminService.AcceptInviteAsync(
                request,
                cancellationToken,
                actor);
            return Ok(result);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException or InvalidOperationException or InviteAcceptanceException or SupabaseAuthAdminException)
        {
            return InviteError(ex);
        }
    }

    private ActionResult InviteError(Exception ex)
    {
        if (ex is InviteAcceptanceException acceptance)
        {
            return StatusCode(acceptance.StatusCode, new { error = acceptance.Code });
        }

        if (ex is KeyNotFoundException)
        {
            return NotFound(new { error = ex.Message });
        }

        if (ex is ArgumentException)
        {
            return BadRequest(new { error = ex.Message });
        }

        if (ex is InvalidOperationException)
        {
            return Conflict(new { error = ex.Message });
        }

        if (ex is SupabaseAuthAdminException admin)
        {
            if (admin.StatusCode is 409 or 422)
            {
                return Conflict(new { error = admin.Message });
            }

            return StatusCode(StatusCodes.Status502BadGateway, new { error = admin.Message });
        }

        throw new InvalidOperationException("Unexpected invite error.", ex);
    }
}
