using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authentication;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Modules.Assets.Services;

namespace Platform.Api.Modules.Admin.Controllers;

[ApiController]
[Authorize(Policy = SupabaseAuthenticationExtensions.PlatformAdminPolicy)]
[Route("api/admin/asset-families")]
public sealed class AdminAssetFamiliesController(IAssetFamilyService assetFamilyService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AssetFamilyDetailResponse>>> ListCatalog(
        [FromQuery] bool includeInactive,
        CancellationToken cancellationToken)
    {
        var families = includeInactive
            ? await assetFamilyService.ListCatalogIncludingInactiveAsync(cancellationToken)
            : await assetFamilyService.ListCatalogAsync(cancellationToken);

        return Ok(families);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AssetFamilyDetailResponse>> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            var family = await assetFamilyService.GetByIdAsync(id, cancellationToken);
            return Ok(family);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost]
    public async Task<ActionResult<AssetFamilyDetailResponse>> Create(
        [FromBody] CreateAssetFamilyRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var family = await assetFamilyService.CreateAsync(request, cancellationToken);
            return Created($"/api/admin/asset-families/{family.Id}", family);
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

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<AssetFamilyDetailResponse>> Update(
        Guid id,
        [FromBody] UpdateAssetFamilyRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var family = await assetFamilyService.UpdateAsync(id, request, cancellationToken);
            return Ok(family);
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

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await assetFamilyService.DeleteAsync(id, cancellationToken);
            return NoContent();
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
