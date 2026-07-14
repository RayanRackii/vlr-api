using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authentication;
using Platform.Api.Modules.CustomerAuth.Dtos;
using Platform.Api.Modules.CustomerAuth.Services;

namespace Platform.Api.Modules.CustomerAuth.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/auth/customer")]
public sealed class CustomerAuthController(
    ICustomerAuthService customerAuthService,
    IPublicTenantBinder publicTenantBinder) : ControllerBase
{
    [HttpPost("request-otp")]
    public async Task<IActionResult> RequestOtp(
        [FromBody] RequestOtpDto request,
        [FromHeader(Name = TenantHeaders.Subdomain)] string? tenantSubdomain,
        CancellationToken cancellationToken)
    {
        try
        {
            await publicTenantBinder.BindFromSubdomainAsync(tenantSubdomain, cancellationToken);
            await customerAuthService.RequestOtpAsync(request, cancellationToken);
            return Accepted(new { message = "OTP sent." });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("verify-otp")]
    public async Task<ActionResult<AuthResponseDto>> VerifyOtp(
        [FromBody] VerifyOtpDto request,
        [FromHeader(Name = TenantHeaders.Subdomain)] string? tenantSubdomain,
        CancellationToken cancellationToken)
    {
        try
        {
            await publicTenantBinder.BindFromSubdomainAsync(tenantSubdomain, cancellationToken);
            var response = await customerAuthService.VerifyOtpAsync(request, cancellationToken);
            return Ok(response);
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
}
