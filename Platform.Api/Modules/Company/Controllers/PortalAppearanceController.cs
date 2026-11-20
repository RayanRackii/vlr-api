using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Common;
using Platform.Api.Modules.Company.Dtos;
using Platform.Api.Modules.Company.Services;
using Platform.Core.Domain.Constants;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Company.Controllers;

/// <summary>Customer portal login appearance of the current tenant (never addressed by id).</summary>
[ApiController]
[Route("api/portal-appearance")]
public sealed class PortalAppearanceController(
    IPortalAppearanceService service,
    ITenantProvider tenantProvider) : ControllerBase
{
    /// <summary>Any signed-in staff member of the tenant may read the appearance.</summary>
    [HttpGet]
    [Authorize]
    public async Task<ActionResult<PortalAppearanceResponse>> GetPortalAppearance(CancellationToken cancellationToken)
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
    public Task<ActionResult<PortalAppearanceResponse>> UpdatePortalAppearance(
        [FromBody] UpdatePortalAppearanceRequest request,
        CancellationToken cancellationToken) =>
        RunWrite(() => service.UpdateTextsAsync(request, cancellationToken));

    [HttpPost("landing-image")]
    [RequirePermission(Permissions.Core.CompanyWrite)]
    [RequestSizeLimit(PortalLandingImageRules.MaxBytes + 64 * 1024)]
    public async Task<ActionResult<PortalAppearanceResponse>> UploadLandingImage(
        IFormFile file,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "File is required." });
        }

        return await RunWrite(async () =>
        {
            await using var stream = file.OpenReadStream();
            return await service.UploadLandingImageAsync(
                file.FileName,
                file.ContentType ?? "application/octet-stream",
                stream,
                cancellationToken);
        });
    }

    [HttpDelete("landing-image")]
    [RequirePermission(Permissions.Core.CompanyWrite)]
    public Task<ActionResult<PortalAppearanceResponse>> DeleteLandingImage(CancellationToken cancellationToken) =>
        RunWrite(() => service.DeleteLandingImageAsync(cancellationToken));

    private async Task<ActionResult<PortalAppearanceResponse>> RunWrite(Func<Task<PortalAppearanceResponse>> action)
    {
        try
        {
            return Ok(await action());
        }
        catch (PortalLandingImageException ex)
        {
            return BadRequest(new { error = ex.Message, code = ex.Code });
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
