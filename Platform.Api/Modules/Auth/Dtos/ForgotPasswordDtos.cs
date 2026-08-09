namespace Platform.Api.Modules.Auth.Dtos;

public sealed record ForgotPasswordRequestDto
{
    public required string Email { get; init; }
}

public sealed record ForgotPasswordResponseDto(string Message);
