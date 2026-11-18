namespace Platform.Api.Modules.Company.Dtos;

public sealed record CompanyAddressDto(
    string? PostalCode,
    string? Street,
    string? Number,
    string? Complement,
    string? District,
    string? City,
    string? State);

/// <summary>Company profile of the current tenant. <c>Subdomain</c> is read-only (platform-managed).</summary>
public sealed record CompanyProfileResponse(
    string LegalName,
    string? TradeName,
    string TaxId,
    string? ContactEmail,
    string? ContactPhone,
    CompanyAddressDto Address,
    string? Subdomain);

public sealed record UpdateCompanyProfileRequest
{
    public string? LegalName { get; init; }

    public string? TradeName { get; init; }

    public string? TaxId { get; init; }

    public string? ContactEmail { get; init; }

    public string? ContactPhone { get; init; }

    public CompanyAddressDto? Address { get; init; }
}
