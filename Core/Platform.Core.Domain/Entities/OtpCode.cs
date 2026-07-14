using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Short-lived passwordless OTP for B2C customer auth. Schema: core.
/// </summary>
public class OtpCode : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid CustomerId { get; set; }

    public required string Code { get; set; }

    public required DateTimeOffset ExpiresAt { get; set; }

    public required bool IsUsed { get; set; }

    public Customer Customer { get; set; } = null!;

    public void MarkAsUsed()
    {
        IsUsed = true;
        MarkAsUpdated();
    }
}
