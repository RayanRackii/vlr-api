using Platform.Core.Domain.Common;
using Platform.Core.Domain.Constants;

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

    /// <summary>
    /// Obsolete: image/URL logos are no longer used. Prefer <see cref="LogoSvg"/>.
    /// Column kept for backward compatibility until dropped in a later migration.
    /// </summary>
    public string? LogoUrl { get; private set; }

    /// <summary>Inline SVG markup for the tenant brand mark (B2C portal).</summary>
    public string? LogoSvg { get; private set; }

    /// <summary>Primary brand color as #RRGGBB for the B2C portal.</summary>
    public string? PrimaryColor { get; private set; }

    /// <summary>Optional accent color as #RRGGBB.</summary>
    public string? AccentColor { get; private set; }

    /// <summary>Short welcome line on the B2C login/register shell (max ~120 chars).</summary>
    public string? WelcomeTagline { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Self-serve trial tenant (limits + lifecycle purge).</summary>
    public bool IsTrial { get; private set; }

    public DateTimeOffset? TrialEndsAt { get; private set; }

    public DateTimeOffset? TrialPurgeAt { get; private set; }

    /// <summary>When true, WhatsApp / non-email notification channels stay off.</summary>
    public bool NotificationsEmailOnly { get; private set; }

    private readonly List<User> _users = [];

    private readonly List<Unit> _units = [];

    private readonly List<Role> _roles = [];

    private readonly List<TenantModule> _modules = [];

    private readonly List<TenantRegistrationField> _registrationFields = [];

    private readonly List<TenantModuleMenuItem> _moduleMenuItems = [];

    public IReadOnlyCollection<User> Users => _users.AsReadOnly();

    public IReadOnlyCollection<Unit> Units => _units.AsReadOnly();

    public IReadOnlyCollection<Role> Roles => _roles.AsReadOnly();

    public IReadOnlyCollection<TenantModule> Modules => _modules.AsReadOnly();

    public IReadOnlyCollection<TenantRegistrationField> RegistrationFields =>
        _registrationFields.AsReadOnly();

    public IReadOnlyCollection<TenantModuleMenuItem> ModuleMenuItems =>
        _moduleMenuItems.AsReadOnly();

    private Tenant()
    {
    }

    public Tenant(
        string legalName,
        string taxId,
        string? tradeName = null,
        string? subdomain = null,
        string? logoSvg = null,
        string? primaryColor = null,
        string? accentColor = null,
        string? welcomeTagline = null)
    {
        LegalName = legalName;
        TaxId = taxId;
        TradeName = tradeName;
        Subdomain = NormalizeSubdomain(subdomain);
        LogoUrl = null;
        LogoSvg = logoSvg;
        PrimaryColor = NormalizeHexColor(primaryColor);
        AccentColor = NormalizeHexColor(accentColor);
        WelcomeTagline = NormalizeTagline(welcomeTagline);
        IsActive = true;
    }

    public void UpdateProfile(
        string legalName,
        string taxId,
        string? tradeName,
        string? subdomain = null,
        string? logoSvg = null,
        string? primaryColor = null,
        string? accentColor = null,
        string? welcomeTagline = null)
    {
        LegalName = legalName;
        TaxId = taxId;
        TradeName = tradeName;
        Subdomain = NormalizeSubdomain(subdomain);
        LogoUrl = null;
        LogoSvg = logoSvg;
        PrimaryColor = NormalizeHexColor(primaryColor);
        AccentColor = NormalizeHexColor(accentColor);
        WelcomeTagline = NormalizeTagline(welcomeTagline);
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

    /// <summary>
    /// Marks this tenant as a self-serve trial. Call after construct when creating a trial.
    /// </summary>
    public void ConfigureAsTrial(DateTimeOffset createdAt)
    {
        IsTrial = true;
        TrialEndsAt = createdAt.AddDays(TrialLimits.TrialDays);
        TrialPurgeAt = createdAt.AddDays(TrialLimits.PurgeDays);
        NotificationsEmailOnly = true;
        MarkAsUpdated();
    }

    /// <summary>
    /// Trial ended but not yet purged: writes should be blocked; reads remain allowed.
    /// </summary>
    public bool IsTrialReadOnly(DateTimeOffset utcNow) =>
        IsTrial
        && TrialEndsAt is not null
        && utcNow >= TrialEndsAt
        && (TrialPurgeAt is null || utcNow < TrialPurgeAt);

    private static string? NormalizeSubdomain(string? subdomain)
    {
        if (string.IsNullOrWhiteSpace(subdomain))
        {
            return null;
        }

        return subdomain.Trim().ToLowerInvariant();
    }

    private static string? NormalizeHexColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return null;
        }

        var trimmed = color.Trim();
        if (trimmed.Length == 7 && trimmed[0] == '#')
        {
            return trimmed.ToUpperInvariant();
        }

        if (trimmed.Length == 6 && trimmed.All(Uri.IsHexDigit))
        {
            return $"#{trimmed.ToUpperInvariant()}";
        }

        throw new ArgumentException("Color must be a hex value like #1A2B3C.");
    }

    private static string? NormalizeTagline(string? tagline)
    {
        if (string.IsNullOrWhiteSpace(tagline))
        {
            return null;
        }

        var trimmed = tagline.Trim();
        return trimmed.Length <= 120 ? trimmed : trimmed[..120];
    }
}
