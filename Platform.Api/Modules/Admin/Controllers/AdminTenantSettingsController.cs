using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authentication;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Modules.Admin.Services;

namespace Platform.Api.Modules.Admin.Controllers;

/// <summary>Platform-admin access to a fixed allow-list of tenant settings (see the service).</summary>
[ApiController]
[Authorize(Policy = SupabaseAuthenticationExtensions.PlatformAdminPolicy)]
[Route("api/admin/tenants/{tenantId:guid}/settings")]
public sealed class AdminTenantSettingsController(
    ITenantSettingsAdminService settings) : ControllerBase
{
    [HttpGet("{key}")]
    public async Task<ActionResult<TenantSettingDto>> Get(
        Guid tenantId,
        string key,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await settings.GetAsync(tenantId, key, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPut("{key}")]
    public async Task<ActionResult<TenantSettingDto>> Put(
        Guid tenantId,
        string key,
        [FromBody] PutTenantSettingRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await settings.PutAsync(tenantId, key, request.Value, cancellationToken));
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
