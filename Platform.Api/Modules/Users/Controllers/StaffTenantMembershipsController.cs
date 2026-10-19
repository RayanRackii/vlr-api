using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Modules.Users.Dtos;
using Platform.Api.Modules.Users.Services;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Modules.Users.Controllers;

/// <summary>
/// Separate from <see cref="UsersController"/> so staff identity routes keep their
/// existing constructor. These actions do not require a permission in the token's
/// current tenant: the caller may be selecting away from a stale tenant.
/// </summary>
[ApiController]
[Authorize]
[Route("api/users")]
public sealed class StaffTenantMembershipsController(
    IStaffTenantMembershipService staffTenantMembershipService) : ControllerBase
{
    /// <summary>
    /// Active staff memberships of the signed-in Auth user. Platform admins use enter/exit.
    /// </summary>
    [HttpGet("me/tenants")]
    public async Task<ActionResult<StaffTenantMembershipListResponse>> ListMemberships(
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await staffTenantMembershipService.ListAsync(User, cancellationToken));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (SupabaseAuthAdminException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Auth service is unavailable." });
        }
    }

    /// <summary>
    /// Points <c>app_metadata.tenant_id</c> at one of the caller's active memberships.
    /// Does not change the password. The current access token keeps its previous
    /// tenant until the client refreshes the Supabase session.
    /// </summary>
    [HttpPost("me/tenant")]
    public async Task<ActionResult<SelectStaffTenantResponse>> SelectMembership(
        [FromBody] SelectStaffTenantRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var selected = await staffTenantMembershipService.SelectAsync(
                User,
                request.TenantId,
                cancellationToken);
            return Ok(selected);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (SupabaseAuthAdminException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Auth service is unavailable." });
        }
    }
}
