using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;

namespace Platform.Api.Modules.Rentals.Controllers;

[ApiController]
[Authorize]
[Route("api/rental-assets")]
public sealed class RentalAssetsController(
    IRentalAssetService rentalAssetService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RentalAssetResponse>>> List(
        CancellationToken cancellationToken)
    {
        var assets = await rentalAssetService.ListRentableAsync(cancellationToken);
        return Ok(assets);
    }

    [HttpGet("by-asset/{assetId:guid}")]
    public async Task<ActionResult<RentalAssetResponse>> GetByAssetId(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var asset = await rentalAssetService.GetByAssetIdAsync(assetId, cancellationToken);

        if (asset is null)
        {
            return NotFound();
        }

        return Ok(asset);
    }

    [HttpPut("schedule-policy")]
    public async Task<ActionResult<BulkUpdateRentalSchedulePolicyResponseDto>> UpdateSchedulePolicyBulk(
        [FromBody] BulkUpdateRentalSchedulePolicyRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(
                await rentalAssetService.UpdateSchedulePolicyBulkAsync(request, cancellationToken));
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

    [HttpPut("{id:guid}/schedule-policy")]
    public async Task<ActionResult<RentalAssetResponse>> UpdateSchedulePolicy(
        Guid id,
        [FromBody] UpdateRentalSchedulePolicyRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(
                await rentalAssetService.UpdateSchedulePolicyAsync(id, request, cancellationToken));
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
