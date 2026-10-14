using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authentication;
using Platform.Api.Modules.Auth.Dtos;
using Platform.Api.Modules.Auth.Services;
using Platform.Api.Modules.CustomerAuth.PhoneVerification;

namespace Platform.Api.Modules.Auth.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class PasswordRecoveryController(
    IPasswordRecoveryService passwordRecoveryService,
    IPublicTenantBinder publicTenantBinder) : ControllerBase
{
    private const string GenericMessage =
        "Se existir uma conta com este e-mail, enviamos um link para redefinir a senha.";

    [AllowAnonymous]
    [HttpPost("forgot-password")]
    public async Task<ActionResult<ForgotPasswordResponseDto>> ForgotPassword(
        [FromBody] ForgotPasswordRequestDto request,
        [FromHeader(Name = TenantHeaders.Subdomain)] string? tenantSubdomain,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return BadRequest(new { error = "Email is required." });
        }

        if (!string.IsNullOrWhiteSpace(tenantSubdomain))
        {
            try
            {
                await publicTenantBinder.BindFromSubdomainAsync(tenantSubdomain, cancellationToken);
            }
            catch (TenantResolutionException)
            {
                // Unknown or inactive slug must not be distinguishable from a missing account.
                return Ok(new ForgotPasswordResponseDto(GenericMessage));
            }
        }

        try
        {
            await passwordRecoveryService.RequestAsync(request.Email, cancellationToken);
            return Ok(new ForgotPasswordResponseDto(GenericMessage));
        }
        catch (PhoneVerificationRateLimitedException ex)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = ex.Message });
        }
    }
}
