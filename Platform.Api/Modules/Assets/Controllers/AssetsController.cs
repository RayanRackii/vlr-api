using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Modules.Assets.Services;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.Assets.Controllers;

[ApiController]
[Route("api/assets")]
public sealed class AssetsController(IAssetService assetService) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Inventory.AssetsRead)]
    public async Task<ActionResult<IReadOnlyList<AssetResponse>>> List(
        CancellationToken cancellationToken)
    {
        var assets = await assetService.ListAsync(cancellationToken);
        return Ok(assets);
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Inventory.AssetsRead)]
    public async Task<ActionResult<AssetResponse>> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        var asset = await assetService.GetByIdAsync(id, cancellationToken);

        if (asset is null)
        {
            return NotFound();
        }

        return Ok(asset);
    }

    [HttpPost]
    [RequirePermission(Permissions.Inventory.AssetsWrite)]
    public async Task<ActionResult<AssetResponse>> Create(
        [FromBody] CreateAssetRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var asset = await assetService.CreateAsync(request, cancellationToken);
            return CreatedAtAction(nameof(GetById), new { id = asset.Id }, asset);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost("bulk")]
    [RequirePermission(Permissions.Inventory.AssetsWrite)]
    public async Task<ActionResult<BulkCreateAssetsResponse>> BulkCreate(
        [FromBody] BulkCreateAssetsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await assetService.BulkCreateAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
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

    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.Inventory.AssetsWrite)]
    public async Task<ActionResult<AssetResponse>> Update(
        Guid id,
        [FromBody] UpdateAssetRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var asset = await assetService.UpdateAsync(id, request, cancellationToken);

            if (asset is null)
            {
                return NotFound();
            }

            return Ok(asset);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(Permissions.Inventory.AssetsWrite)]
    public async Task<ActionResult<DeleteAssetResult>> Delete(
        Guid id,
        CancellationToken cancellationToken)
    {
        var result = await assetService.DeleteAsync(id, cancellationToken);

        if (result is null)
        {
            return NotFound();
        }

        if (result.PermanentlyDeleted)
        {
            return NoContent();
        }

        return Ok(result);
    }
}
