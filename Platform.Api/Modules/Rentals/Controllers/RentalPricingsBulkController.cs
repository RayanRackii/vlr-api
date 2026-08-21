using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;

namespace Platform.Api.Modules.Rentals.Controllers;

[ApiController]
[Authorize]
[Route("api/assets")]
public sealed class RentalPricingsBulkController(
    IRentalPricingService rentalPricingService) : ControllerBase
{
    [HttpPost("pricing-bulk")]
    public async Task<ActionResult<BulkApplyPricingsResponse>> ApplyBulk(
        [FromBody] BulkApplyPricingsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await rentalPricingService.ApplyBulkAsync(request, cancellationToken);
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
}
