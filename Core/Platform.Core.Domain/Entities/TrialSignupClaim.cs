using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// One-time claim preventing repeat self-serve trials for the same email or phone.
/// </summary>
public class TrialSignupClaim : Entity
{
    public string EmailNormalized { get; private set; } = null!;

    public string PhoneNormalized { get; private set; } = null!;

    public Guid? TenantId { get; private set; }

    private TrialSignupClaim()
    {
    }

    public TrialSignupClaim(string emailNormalized, string phoneNormalized)
    {
        EmailNormalized = emailNormalized;
        PhoneNormalized = phoneNormalized;
    }

    public void LinkTenant(Guid tenantId)
    {
        TenantId = tenantId;
        MarkAsUpdated();
    }
}
