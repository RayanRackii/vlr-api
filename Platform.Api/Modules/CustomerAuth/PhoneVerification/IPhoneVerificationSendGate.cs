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

    /// <summary>
    /// Decides whether the caller may send. Every decision except
    /// <see cref="PhoneVerificationSendDecision.Limited"/> consumes one IP-window attempt.
    /// A <see cref="PhoneVerificationSendDecision.Send"/> stores the per-key cooldown
    /// immediately, before the caller knows whether the address exists.
    /// </summary>
    PhoneVerificationSendDecision Decide(
        Guid tenantId,
        string normalizedEmail,
        string? clientIp,
        string scope = VerificationScope);

    /// <summary>
    /// Refreshes the per-key cooldown. <see cref="Decide"/> already stored it on Send,
    /// so this must not be the only write and must not run only when an account exists.
    /// </summary>
    void RecordSuccess(
        Guid tenantId,
        string normalizedEmail,
        string? clientIp,
        string scope = VerificationScope);
}
