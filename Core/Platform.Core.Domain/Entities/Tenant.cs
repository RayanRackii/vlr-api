using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

public class Tenant : Entity
{
    /// <summary>Legal / company name (maps to product "Name").</summary>
    public string LegalName { get; private set; } = null!;

    public string? TradeName { get; private set; }

    /// <summary>CNPJ/CPF or equivalent tax document (maps to product "Document").</summary>
    public string TaxId { get; private set; } = null!;

    /// <summary>Optional subdomain / custom domain used to resolve this Tenant.</summary>
    public string? Subdomain { get; private set; }

    /// <summary>Optional public URL for the tenant logo.</summary>
    public string? LogoUrl { get; private set; }

    public bool IsActive { get; private set; }

    private readonly List<User> _users = [];

    private readonly List<Unit> _units = [];

    private readonly List<Role> _roles = [];

    private readonly List<TenantModule> _modules = [];

    public IReadOnlyCollection<User> Users => _users.AsReadOnly();

    public IReadOnlyCollection<Unit> Units => _units.AsReadOnly();

    public IReadOnlyCollection<Role> Roles => _roles.AsReadOnly();

    public IReadOnlyCollection<TenantModule> Modules => _modules.AsReadOnly();

    private Tenant()
    {
    }

    public Tenant(
        string legalName,
        string taxId,
        string? tradeName = null,
        string? subdomain = null,
        string? logoUrl = null)
    {
        LegalName = legalName;
        TaxId = taxId;
        TradeName = tradeName;
        Subdomain = NormalizeSubdomain(subdomain);
        LogoUrl = NormalizeOptionalUrl(logoUrl);
        IsActive = true;
    }

    public void UpdateProfile(
        string legalName,
        string taxId,
        string? tradeName,
        string? subdomain = null,
        string? logoUrl = null)
    {
        LegalName = legalName;
        TaxId = taxId;
        TradeName = tradeName;
        Subdomain = NormalizeSubdomain(subdomain);
        LogoUrl = NormalizeOptionalUrl(logoUrl);
        MarkAsUpdated();
    }

    public void Activate()
    {
        IsActive = true;
        MarkAsUpdated();
    }

    public void Deactivate()
    {
        IsActive = false;
        MarkAsUpdated();
    }

    private static string? NormalizeSubdomain(string? subdomain)
    {
        if (string.IsNullOrWhiteSpace(subdomain))
        {
            return null;
        }

        return subdomain.Trim().ToLowerInvariant();
    }

    private static string? NormalizeOptionalUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return url.Trim();
    }
}
