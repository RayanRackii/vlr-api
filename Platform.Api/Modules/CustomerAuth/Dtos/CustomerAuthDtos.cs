using System.Text.Json;
using System.Text.Json.Serialization;
using Platform.Core.Domain.Enums;

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

    public required CustomerType CustomerType { get; init; }

    public required string Document { get; init; }

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

public sealed record ResendVerificationRequestDto
{
    public required string Email { get; init; }
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
    CustomerType CustomerType,
    string? Document,
    string? Cpf,
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

public sealed record CustomerProfileDto(
    Guid Id,
    Guid TenantId,
    string Name,
    string? Email,
    string? Phone,
    CustomerType CustomerType,
    string? Document,
    string? Cpf,
    string? PostalCode,
    string? AddressStreet,
    string? AddressNeighborhood,
    string? AddressCity,
    string? AddressState,
    string? PhotoUrl,
    DateTimeOffset CreatedAt,
    bool PhoneVerified,
    IReadOnlyDictionary<string, string?> ExtraAttributes);

public sealed record UpdateCustomerProfileRequestDto
{
    private string? _name;
    private string? _photoUrl;

    [JsonIgnore]
    public bool NameSpecified { get; private set; }

    [JsonIgnore]
    public bool PhotoUrlSpecified { get; private set; }

    public string? Name
    {
        get => _name;
        init
        {
            _name = value;
            NameSpecified = true;
        }
    }

    public string? PhotoUrl
    {
        get => _photoUrl;
        init
        {
            _photoUrl = value;
            PhotoUrlSpecified = true;
        }
    }
}
