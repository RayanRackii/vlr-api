using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authentication;
using Platform.Api.Modules.CustomerAuth.Dtos;
using Platform.Api.Modules.CustomerAuth.Services;
using Platform.Api.Modules.RegistrationFields.Dtos;
using Platform.Api.Modules.RegistrationFields.Services;

namespace Platform.Api.Modules.CustomerAuth.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/auth/customer")]
public sealed class CustomerAuthController(
    ICustomerAuthService customerAuthService,
    IRegistrationFieldService registrationFieldService,
    IPublicTenantBinder publicTenantBinder) : ControllerBase
{
    [HttpGet("~/api/public/tenants/{subdomain}/branding")]
    public async Task<ActionResult<TenantBrandingResponseDto>> GetBranding(
        string subdomain,
        CancellationToken cancellationToken)
    {
        try
        {
            var branding = await customerAuthService.GetBrandingAsync(
                subdomain,
                cancellationToken);
            return Ok(branding);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpGet("~/api/public/tenants/{subdomain}/registration-schema")]
    public async Task<ActionResult<RegistrationSchemaResponseDto>> GetRegistrationSchema(
        string subdomain,
        CancellationToken cancellationToken)
    {
        try
        {
            var schema = await registrationFieldService.GetSchemaBySubdomainAsync(
                subdomain,
                cancellationToken);
            return Ok(schema);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost("register")]
    public async Task<ActionResult<RegisterCustomerResponseDto>> Register(
        [FromBody] RegisterCustomerRequestDto request,
        [FromHeader(Name = TenantHeaders.Subdomain)] string? tenantSubdomain,
        CancellationToken cancellationToken)
    {
        try
        {
            await publicTenantBinder.BindFromSubdomainAsync(tenantSubdomain, cancellationToken);
            var response = await customerAuthService.RegisterAsync(request, cancellationToken);
            return Ok(response);
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

    [HttpPost("verify-phone")]
    public async Task<ActionResult<AuthResponseDto>> VerifyPhone(
        [FromBody] VerifyPhoneRequestDto request,
        [FromHeader(Name = TenantHeaders.Subdomain)] string? tenantSubdomain,
        CancellationToken cancellationToken)
    {
        try
        {
            await publicTenantBinder.BindFromSubdomainAsync(tenantSubdomain, cancellationToken);
            var response = await customerAuthService.VerifyPhoneAsync(request, cancellationToken);
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

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponseDto>> Login(
        [FromBody] CustomerLoginRequestDto request,
        [FromHeader(Name = TenantHeaders.Subdomain)] string? tenantSubdomain,
        CancellationToken cancellationToken)
    {
        try
        {
            await publicTenantBinder.BindFromSubdomainAsync(tenantSubdomain, cancellationToken);
            var response = await customerAuthService.LoginAsync(request, cancellationToken);
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
