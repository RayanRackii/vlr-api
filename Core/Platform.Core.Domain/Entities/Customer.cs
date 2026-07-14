using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// End-customer (B2C) of a Tenant. Schema: core.
/// </summary>
public class Customer : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required string Name { get; set; }

    /// <summary>Primary phone (WhatsApp). Required when contact is phone-based.</summary>
    public string? Phone { get; set; }

    public string? Email { get; set; }

    private readonly List<OtpCode> _otpCodes = [];

    public IReadOnlyCollection<OtpCode> OtpCodes => _otpCodes.AsReadOnly();
}
