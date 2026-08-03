using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// End-customer (B2C) of a Tenant. Schema: core.
/// Authenticates with email + password. Phone is SMS-verified for WhatsApp later.
/// </summary>
public class Customer : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required string Name { get; set; }

    /// <summary>Login email. Unique per tenant when set.</summary>
    public string? Email { get; set; }

    /// <summary>ASP.NET Identity password hash. Null for legacy OTP-only rows.</summary>
    public string? PasswordHash { get; set; }

    /// <summary>Primary mobile (E.164-ish digits). Unique per tenant when set.</summary>
    public string? Phone { get; set; }

    public DateTimeOffset? PhoneVerifiedAt { get; set; }

    /// <summary>Brazilian CPF digits only (11).</summary>
    public string? Cpf { get; set; }

    /// <summary>Brazilian postal code digits only (8).</summary>
    public string? PostalCode { get; set; }

    public string? AddressStreet { get; set; }

    public string? AddressNeighborhood { get; set; }

    public string? AddressCity { get; set; }

    public string? AddressState { get; set; }

    /// <summary>Profile photo URL or data URL.</summary>
    public string? PhotoUrl { get; set; }

    private readonly List<OtpCode> _otpCodes = [];

    public IReadOnlyCollection<OtpCode> OtpCodes => _otpCodes.AsReadOnly();

    public bool IsPhoneVerified => PhoneVerifiedAt is not null;

    public void MarkPhoneVerified(DateTimeOffset at)
    {
        PhoneVerifiedAt = at;
        Touch();
    }
}
