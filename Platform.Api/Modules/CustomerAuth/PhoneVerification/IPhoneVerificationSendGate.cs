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

    /// <summary>
    /// Staff <c>POST /api/auth/forgot-password</c>. Separate from customer reset so the two
    /// inboxes do not share a cooldown or an IP window.
    /// </summary>
    public const string StaffPasswordRecoveryScope = "staff-password-recovery";

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
