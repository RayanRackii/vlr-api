namespace Platform.Api.Modules.CustomerAuth.Dtos;

public sealed record RequestOtpDto
{
    public required string Name { get; init; }

    public required string Contact { get; init; }
}

public sealed record VerifyOtpDto
{
    public required string Contact { get; init; }

    public required string Code { get; init; }
}

public sealed record CustomerAuthProfileDto(
    Guid Id,
    Guid TenantId,
    string Name,
    string? Phone,
    string? Email,
    DateTimeOffset CreatedAt);

public sealed record AuthResponseDto(
    string Token,
    CustomerAuthProfileDto Customer);
