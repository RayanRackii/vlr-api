using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;

namespace Platform.Api.Modules.Rentals.Controllers;

[ApiController]
[Authorize]
[Route("api/assets/{assetId:guid}/pricing")]
public sealed class RentalPricingsController(
    IRentalPricingService rentalPricingService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RentalPricingResponseDto>>> List(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        try
        {
            var pricings = await rentalPricingService.GetByAssetIdAsync(assetId, cancellationToken);
            return Ok(pricings);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost]
    public async Task<ActionResult<RentalPricingResponseDto>> Create(
        Guid assetId,
        [FromBody] CreateRentalPricingDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var pricing = await rentalPricingService.CreateAsync(assetId, request, cancellationToken);
            return Created(
                $"/api/assets/{assetId}/pricing/{pricing.Id}",
                pricing);
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

    [HttpPut("{pricingId:guid}")]
    public async Task<ActionResult<RentalPricingResponseDto>> Update(
        Guid assetId,
        Guid pricingId,
        [FromBody] UpdateRentalPricingDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var pricing = await rentalPricingService.UpdateAsync(
                assetId,
                pricingId,
                request,
                cancellationToken);

            if (pricing is null)
            {
                return NotFound();
            }

            return Ok(pricing);
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

    [HttpDelete("{pricingId:guid}")]
    public async Task<IActionResult> Delete(
        Guid assetId,
        Guid pricingId,
        CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await rentalPricingService.DeleteAsync(assetId, pricingId, cancellationToken);

            if (!deleted)
            {
                return NotFound();
            }

            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }
}
