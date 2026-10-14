namespace Platform.Api.Modules.Auth.Services;

public interface IPasswordRecoveryService
{
    /// <summary>
    /// Sends a Rolvix recovery email only when the address is an active user of the current
    /// tenant and an Auth account exists. No current tenant, an inactive membership, or an
    /// unknown address completes without an email. The caller still cannot tell those cases
    /// apart. A per-email cooldown returns the same way; the IP window throws
    /// <see cref="Platform.Api.Modules.CustomerAuth.PhoneVerification.PhoneVerificationRateLimitedException"/>.
    /// </summary>
    Task RequestAsync(string email, CancellationToken cancellationToken);
}
