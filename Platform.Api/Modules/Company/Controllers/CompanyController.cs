using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Common;
using Platform.Api.Modules.Company.Dtos;
using Platform.Api.Modules.Company.Services;
using Platform.Core.Domain.Constants;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Company.Controllers;

/// <summary>Company profile of the current tenant (never addressed by id).</summary>
[ApiController]
[Route("api/company")]
public sealed class CompanyController(
    ICompanyProfileService service,
    ITenantProvider tenantProvider) : ControllerBase
{
    /// <summary>Any signed-in staff member of the tenant may read the profile (the screen is read-only for them).</summary>
    [HttpGet]
    [Authorize]
    public async Task<ActionResult<CompanyProfileResponse>> Get(CancellationToken cancellationToken)
    {
        var missingTenant = DirectoryAuthorization.RequireTenant(this, tenantProvider);
        if (missingTenant is not null)
        {
            return missingTenant;
        }

        try
        {
            return Ok(await service.GetAsync(cancellationToken));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPut]
    [RequirePermission(Permissions.Core.CompanyWrite)]
    public async Task<ActionResult<CompanyProfileResponse>> Update(
        [FromBody] UpdateCompanyProfileRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await service.UpdateAsync(request, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }
}
