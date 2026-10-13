namespace Platform.Api.Modules.CustomerAuth.PhoneVerification;

public enum PhoneVerificationSendDecision
{
    Send,
    Cooldown,
    Limited
}

public interface IPhoneVerificationSendGate
{
    public const string VerificationScope = "phone-verify";

    public const string PasswordResetScope = "password-reset";

    public const string PasswordResetConfirmScope = "password-reset-confirm";

    PhoneVerificationSendDecision Decide(
        Guid tenantId,
        string normalizedEmail,
        string? clientIp,
        string scope = VerificationScope);

    void RecordSuccess(
        Guid tenantId,
        string normalizedEmail,
        string? clientIp,
        string scope = VerificationScope);
}
