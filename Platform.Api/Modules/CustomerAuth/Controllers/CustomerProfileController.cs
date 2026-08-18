using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authentication;
using Platform.Api.Modules.CustomerAuth.Dtos;
using Platform.Api.Modules.CustomerAuth.Services;

namespace Platform.Api.Modules.CustomerAuth.Controllers;

[ApiController]
[Authorize(Policy = "Customer")]
[Route("api/customers")]
public sealed class CustomerProfileController(
    ICustomerAuthService customerAuthService) : ControllerBase
{
    [HttpGet("me")]
    public async Task<ActionResult<CustomerProfileDto>> GetMe(CancellationToken cancellationToken)
    {
        try
        {
            var profile = await customerAuthService.GetCurrentAsync(
                ResolveCustomerId(),
                cancellationToken);
            return Ok(profile);
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

    [HttpPatch("me")]
    public async Task<ActionResult<CustomerProfileDto>> UpdateMe(
        [FromBody] UpdateCustomerProfileRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var profile = await customerAuthService.UpdateProfileAsync(
                ResolveCustomerId(),
                request,
                cancellationToken);
            return Ok(profile);
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
    }

    private Guid ResolveCustomerId()
    {
        var customerIdClaim = User.FindFirst(CustomerClaimTypes.CustomerId)?.Value;

        if (!Guid.TryParse(customerIdClaim, out var customerId))
        {
            throw new UnauthorizedAccessException(
                "The access token is missing a valid customer_id claim.");
        }

        return customerId;
    }
}
