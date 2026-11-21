namespace Platform.Api.Features.CreateTenant;

public sealed record CreateTenantRequest(
    string LegalName,
    string TaxId,
    string? TradeName,
    string HeadquartersUnitName,
    string? HeadquartersUnitCode,
    string AdminFullName,
    string AdminEmail,
    string? AdminPassword = null,
    string? AdminPhone = null,
    bool IsTrial = false,
    string? PackageKey = null,
    string? LeadSource = null,
    string? LeadCampaign = null);
