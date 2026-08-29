using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.Rentals.Controllers;

[ApiController]
[Route("api/assets")]
public sealed class RentalPricingsBulkController(
    IRentalPricingService rentalPricingService) : ControllerBase
{
    [HttpPost("pricing-bulk")]
    [RequirePermission(Permissions.Rentals.PricingBulkWrite)]
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
