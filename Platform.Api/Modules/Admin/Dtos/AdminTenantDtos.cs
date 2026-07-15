namespace Platform.Api.Modules.Admin.Dtos;

public sealed record CreateTenantRequestDto
{
    public required string LegalName { get; init; }

    public required string TaxId { get; init; }

    public required string Subdomain { get; init; }

    public string? LogoUrl { get; init; }

    /// <summary>Module labels: Rentals, PMOC, Inventory, OS.</summary>
    public required IReadOnlyList<string> ActiveModules { get; init; }
}

public sealed record UpdateTenantRequestDto
{
    public required string LegalName { get; init; }

    public required string TaxId { get; init; }

    public required string Subdomain { get; init; }

    public string? LogoUrl { get; init; }

    /// <summary>Module labels: Rentals, PMOC, Inventory, OS.</summary>
    public required IReadOnlyList<string> ActiveModules { get; init; }
}

public sealed record TenantModuleResponseDto(
    string ModuleName,
    bool IsActive);

public sealed record TenantAdminResponseDto(
    Guid Id,
    string LegalName,
    string TaxId,
    string? Subdomain,
    string? LogoUrl,
    bool IsActive,
    DateTimeOffset CreatedAt,
    IReadOnlyList<TenantModuleResponseDto> ActiveModules);
