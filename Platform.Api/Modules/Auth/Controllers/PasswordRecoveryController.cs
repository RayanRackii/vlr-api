using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Modules.Auth.Dtos;
using Platform.Api.Modules.Auth.Services;

namespace Platform.Api.Modules.Auth.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class PasswordRecoveryController(
    IPasswordRecoveryService passwordRecoveryService) : ControllerBase
{
    private const string GenericMessage =
        "Se existir uma conta com este e-mail, enviamos um link para redefinir a senha.";

    [AllowAnonymous]
    [HttpPost("forgot-password")]
    public async Task<ActionResult<ForgotPasswordResponseDto>> ForgotPassword(
        [FromBody] ForgotPasswordRequestDto request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return BadRequest(new { error = "Email is required." });
        }

        await passwordRecoveryService.RequestAsync(request.Email, cancellationToken);
        return Ok(new ForgotPasswordResponseDto(GenericMessage));
    }
}
