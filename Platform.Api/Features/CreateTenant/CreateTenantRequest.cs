namespace Platform.Api.Features.CreateTenant;

public sealed record CreateTenantRequest(
    string LegalName,
    string TaxId,
    string? TradeName,
    string HeadquartersUnitName,
    string? HeadquartersUnitCode,
    string AdminFullName,
    string AdminEmail,
    string AdminPassword);
