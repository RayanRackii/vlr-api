using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authentication;
using Platform.Api.Modules.Billing.Dtos;
using Platform.Api.Modules.Billing.Services;

namespace Platform.Api.Modules.Billing.Controllers;

/// <summary>Platform-admin management of the subscription of one tenant.</summary>
[ApiController]
[Authorize(Policy = SupabaseAuthenticationExtensions.PlatformAdminPolicy)]
[Route("api/admin/tenants/{tenantId:guid}/subscription")]
public sealed class AdminTenantSubscriptionsController(IAdminSubscriptionService subscriptions) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<TenantSubscriptionDto>> Get(Guid tenantId, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await subscriptions.GetAsync(tenantId, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPut]
    public async Task<ActionResult<TenantSubscriptionDto>> Put(
        Guid tenantId,
        [FromBody] PutTenantSubscriptionRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var actor = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
            return Ok(await subscriptions.PutAsync(tenantId, request, actor, cancellationToken));
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
}
