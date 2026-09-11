using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Short-lived OTP for B2C customer auth. Schema: core.
/// New email-verification rows store HMAC in <see cref="CodeHash"/> and leave <see cref="Code"/> null.
/// </summary>
public class OtpCode : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid CustomerId { get; set; }

    /// <summary>Legacy plaintext digits. Null for new email-verification rows.</summary>
    public string? Code { get; set; }

    public required string Purpose { get; set; }

    public string? CodeHash { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset? ReplacedAt { get; set; }

    public required DateTimeOffset ExpiresAt { get; set; }

    public required bool IsUsed { get; set; }

    public Customer Customer { get; set; } = null!;

    public void MarkAsUsed()
    {
        IsUsed = true;
        MarkAsUpdated();
    }
}
