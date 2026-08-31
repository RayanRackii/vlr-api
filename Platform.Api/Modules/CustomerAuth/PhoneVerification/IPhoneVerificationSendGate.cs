namespace Platform.Api.Modules.CustomerAuth.PhoneVerification;

public enum PhoneVerificationSendDecision
{
    Send,
    Cooldown,
    Limited
}

public interface IPhoneVerificationSendGate
{
    PhoneVerificationSendDecision Decide(Guid tenantId, string normalizedEmail, string? clientIp);

    void RecordSuccess(Guid tenantId, string normalizedEmail, string? clientIp);
}
