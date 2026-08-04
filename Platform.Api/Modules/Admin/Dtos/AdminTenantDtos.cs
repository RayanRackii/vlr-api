namespace Platform.Api.Modules.Admin.Dtos;

public sealed record CreateTenantRequestDto
{
    public required string LegalName { get; init; }

    public required string TaxId { get; init; }

    public required string Subdomain { get; init; }

    /// <summary>Inline SVG brand mark (preferred). Image URLs are no longer accepted.</summary>
    public string? LogoSvg { get; init; }

    public string? PrimaryColor { get; init; }

    public string? AccentColor { get; init; }

    public string? WelcomeTagline { get; init; }

    /// <summary>Module labels: Rentals, PMOC, Inventory, OS.</summary>
    public required IReadOnlyList<string> ActiveModules { get; init; }

    /// <summary>Optional first B2B admin invite (no password — golden rule).</summary>
    public string? AdminFullName { get; init; }

    public string? AdminEmail { get; init; }
}

public sealed record UpdateTenantRequestDto
{
    public required string LegalName { get; init; }

    public required string TaxId { get; init; }

    public required string Subdomain { get; init; }

    public string? LogoSvg { get; init; }

    public string? PrimaryColor { get; init; }

    public string? AccentColor { get; init; }

    public string? WelcomeTagline { get; init; }

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
    string? LogoSvg,
    string? PrimaryColor,
    string? AccentColor,
    string? WelcomeTagline,
    bool IsActive,
    DateTimeOffset CreatedAt,
    IReadOnlyList<TenantModuleResponseDto> ActiveModules);
