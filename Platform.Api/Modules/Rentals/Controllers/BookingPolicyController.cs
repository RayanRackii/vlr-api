using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.Rentals.Controllers;

[ApiController]
[RequireActiveModule(PlatformModules.Rentals)]
[Route("api/rentals/booking-policy")]
public sealed class BookingPolicyController(
    IBookingPolicyService bookingPolicyService,
    IPublicTenantBinder publicTenantBinder) : ControllerBase
{
    [RequirePermission(Permissions.Rentals.BookingPolicyRead)]
    [HttpGet]
    public async Task<ActionResult<BookingPolicyResponseDto>> Get(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await bookingPolicyService.GetAsync(cancellationToken));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Replaces the tenant default booking policy.
    /// Omitted or null <c>cancellationPolicyText</c> and
    /// <c>maxActiveReservationsPerCustomerPerDay</c> clear those stored values.
    /// Omitted <c>maxAdvanceBookingDays</c> keeps the stored horizon.
    /// Explicit null clears it (unlimited).
    /// </summary>
    [RequirePermission(Permissions.Rentals.BookingPolicyWrite)]
    [HttpPut]
    public async Task<ActionResult<BookingPolicyResponseDto>> Upsert(
        [FromBody] UpsertBookingPolicyRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await bookingPolicyService.UpsertAsync(request, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Portal read of the tenant default booking policy. Applies to every bookable resource type.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("~/api/public/tenants/{subdomain}/booking-policy")]
    public async Task<ActionResult<BookingPolicyResponseDto>> GetPublic(
        string subdomain,
        CancellationToken cancellationToken)
    {
        try
        {
            await publicTenantBinder.BindFromSubdomainAsync(subdomain, cancellationToken);
            return Ok(await bookingPolicyService.GetAsync(cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }
}
