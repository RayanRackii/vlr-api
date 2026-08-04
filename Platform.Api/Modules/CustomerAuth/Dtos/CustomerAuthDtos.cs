using System.Text.Json;

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

public sealed record RegisterCustomerRequestDto
{
    public required string Name { get; init; }

    public required string Email { get; init; }

    public required string Password { get; init; }

    public required string Phone { get; init; }

    /// <summary>Values for tenant-defined registration fields.</summary>
    public Dictionary<string, JsonElement>? Attributes { get; init; }
}

public sealed record RegisterCustomerResponseDto(
    Guid CustomerId,
    bool RequiresPhoneVerification);

public sealed record VerifyPhoneRequestDto
{
    public required string Email { get; init; }

    public required string Code { get; init; }
}

public sealed record CustomerLoginRequestDto
{
    public required string Email { get; init; }

    public required string Password { get; init; }
}

public sealed record CustomerAuthProfileDto(
    Guid Id,
    Guid TenantId,
    string Name,
    string? Phone,
    string? Email,
    DateTimeOffset CreatedAt,
    bool PhoneVerified,
    string? PhotoUrl,
    IReadOnlyDictionary<string, string?> ExtraAttributes);

public sealed record AuthResponseDto(
    string Token,
    CustomerAuthProfileDto Customer);

public sealed record TenantBrandingResponseDto(
    string Subdomain,
    string DisplayName,
    string? LogoSvg,
    string? PrimaryColor,
    string? AccentColor,
    string? WelcomeTagline);
