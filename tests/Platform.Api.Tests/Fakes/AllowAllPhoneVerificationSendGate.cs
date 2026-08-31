using Platform.Api.Modules.CustomerAuth.PhoneVerification;

namespace Platform.Api.Tests.Fakes;

public sealed class AllowAllPhoneVerificationSendGate : IPhoneVerificationSendGate
{
    public PhoneVerificationSendDecision Decide(
        Guid tenantId,
        string normalizedEmail,
        string? clientIp) =>
        PhoneVerificationSendDecision.Send;

    public void RecordSuccess(Guid tenantId, string normalizedEmail, string? clientIp)
    {
    }
}
