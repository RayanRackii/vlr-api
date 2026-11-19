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

    /// <summary>Company contact e-mail (staff-editable company profile).</summary>
    public string? ContactEmail { get; private set; }

    /// <summary>Company contact phone, normalized E.164 (+55...).</summary>
    public string? ContactPhone { get; private set; }

    /// <summary>CEP, 8 digits.</summary>
    public string? AddressPostalCode { get; private set; }

    public string? AddressStreet { get; private set; }

    public string? AddressNumber { get; private set; }

    public string? AddressComplement { get; private set; }

    public string? AddressDistrict { get; private set; }

    public string? AddressCity { get; private set; }

    /// <summary>UF, 2 uppercase letters.</summary>
    public string? AddressState { get; private set; }

    /// <summary>Storage key (public bucket) of the customer portal login hero image.</summary>
    public string? LandingImageKey { get; private set; }

    /// <summary>Small uppercase line above the portal login headline (max 60).</summary>
    public string? LandingKicker { get; private set; }

    /// <summary>Portal login headline (max 120).</summary>
    public string? LandingHeadline { get; private set; }

    /// <summary>Portal login subtitle (max 240).</summary>
    public string? LandingSubtitle { get; private set; }

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

    /// <summary>
    /// Staff-editable company profile. Callers pass already-normalized document, phone and CEP
    /// (see <c>CompanyProfileService</c>); this method trims, blanks to null and enforces shape/length.
    /// Platform-only fields (subdomain, branding, trial, active) are untouched.
    /// </summary>
    public void UpdateCompanyProfile(
        string legalName,
        string? tradeName,
        string taxId,
        string? contactEmail,
        string? contactPhone,
        string? postalCode,
        string? street,
        string? number,
        string? complement,
        string? district,
        string? city,
        string? state)
    {
        var legal = legalName?.Trim();
        if (string.IsNullOrEmpty(legal))
        {
            throw new ArgumentException("Legal name is required.");
        }

        var tax = taxId?.Trim();
        if (string.IsNullOrEmpty(tax) || !tax.All(char.IsAsciiDigit) || tax.Length is not (11 or 14))
        {
            throw new ArgumentException("Tax id must be a CPF (11 digits) or CNPJ (14 digits).");
        }

        LegalName = Limit(legal, 200, "Legal name");
        TaxId = tax;
        ApplyContactAndAddress(
            tradeName, contactEmail, contactPhone, postalCode, street, number, complement, district, city, state);
        MarkAsUpdated();
    }

    /// <summary>
    /// Platform-admin edit of trade name, contact and address only (legal name, tax id and platform
    /// fields untouched). Same trimming, shape and length rules as <see cref="UpdateCompanyProfile"/>.
    /// </summary>
    public void UpdateContactAndAddress(
        string? tradeName,
        string? contactEmail,
        string? contactPhone,
        string? postalCode,
        string? street,
        string? number,
        string? complement,
        string? district,
        string? city,
        string? state)
    {
        ApplyContactAndAddress(
            tradeName, contactEmail, contactPhone, postalCode, street, number, complement, district, city, state);
        MarkAsUpdated();
    }

    private void ApplyContactAndAddress(
        string? tradeName,
        string? contactEmail,
        string? contactPhone,
        string? postalCode,
        string? street,
        string? number,
        string? complement,
        string? district,
        string? city,
        string? state)
    {
        var uf = BlankToNull(state)?.ToUpperInvariant();
        if (uf is not null && (uf.Length != 2 || !uf.All(char.IsAsciiLetter)))
        {
            throw new ArgumentException("State must be a 2-letter UF.");
        }

        var cep = BlankToNull(postalCode);
        if (cep is not null && (cep.Length != 8 || !cep.All(char.IsAsciiDigit)))
        {
            throw new ArgumentException("CEP must have 8 digits.");
        }

        TradeName = Limit(BlankToNull(tradeName), 200, "Trade name");
        ContactEmail = Limit(BlankToNull(contactEmail), 254, "E-mail");
        ContactPhone = Limit(BlankToNull(contactPhone), 20, "Phone");
        AddressPostalCode = cep;
        AddressStreet = Limit(BlankToNull(street), 200, "Street");
        AddressNumber = Limit(BlankToNull(number), 20, "Number");
        AddressComplement = Limit(BlankToNull(complement), 100, "Complement");
        AddressDistrict = Limit(BlankToNull(district), 100, "District");
        AddressCity = Limit(BlankToNull(city), 100, "City");
        AddressState = uf;
    }

    /// <summary>Staff-editable portal login texts. Trims, blanks to null and enforces length limits.</summary>
    public void UpdateLandingTexts(string? kicker, string? headline, string? subtitle)
    {
        var newKicker = Limit(BlankToNull(kicker), 60, "Kicker");
        var newHeadline = Limit(BlankToNull(headline), 120, "Headline");
        var newSubtitle = Limit(BlankToNull(subtitle), 240, "Subtitle");
        LandingKicker = newKicker;
        LandingHeadline = newHeadline;
        LandingSubtitle = newSubtitle;
        MarkAsUpdated();
    }

    /// <summary>Sets (or clears with null) the portal login hero image storage key.</summary>
    public void SetLandingImageKey(string? key)
    {
        LandingImageKey = Limit(BlankToNull(key), 300, "Landing image key");
        MarkAsUpdated();
    }

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Limit(string? value, int max, string field) =>
        value is not null && value.Length > max
            ? throw new ArgumentException($"{field} must be at most {max} characters.")
            : value;

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
