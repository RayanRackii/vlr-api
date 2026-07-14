using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authentication;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;

namespace Platform.Api.Modules.Rentals.Controllers;

[ApiController]
[Route("api/reservations")]
public sealed class ReservationsController(
    IReservationService reservationService,
    IPublicTenantBinder publicTenantBinder) : ControllerBase
{
    /// <summary>
    /// Public B2C availability check. Resolve tenant via X-Tenant-Subdomain header
    /// (or authenticated JWT tenant when the header is omitted).
    /// </summary>
    [AllowAnonymous]
    [HttpGet("availability")]
    public async Task<ActionResult<CheckAvailabilityResponseDto>> CheckAvailability(
        [FromQuery] CheckAvailabilityRequestDto request,
        [FromHeader(Name = TenantHeaders.Subdomain)] string? tenantSubdomain,
        CancellationToken cancellationToken)
    {
        try
        {
            await BindPublicTenantIfNeededAsync(tenantSubdomain, cancellationToken);
            var result = await reservationService.CheckAvailabilityAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Create reservation for the authenticated B2C customer.
    /// Tenant comes from the Customer JWT (tenant_id claim).
    /// </summary>
    [Authorize(Policy = "Customer")]
    [HttpPost]
    public async Task<ActionResult<ReservationResponseDto>> Create(
        [FromBody] CreateReservationRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var customerId = ResolveCustomerId();
            var reservation = await reservationService.CreateReservationAsync(
                customerId,
                request,
                cancellationToken);

            return Created($"/api/reservations/{reservation.Id}", reservation);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
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

    private async Task BindPublicTenantIfNeededAsync(
        string? tenantSubdomain,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(tenantSubdomain))
        {
            await publicTenantBinder.BindFromSubdomainAsync(tenantSubdomain, cancellationToken);
            return;
        }

        if (User.Identity?.IsAuthenticated == true)
        {
            return;
        }

        await publicTenantBinder.BindFromSubdomainAsync(null, cancellationToken);
    }
}
