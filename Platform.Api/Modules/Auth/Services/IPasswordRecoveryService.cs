namespace Platform.Api.Modules.Auth.Services;

public interface IPasswordRecoveryService
{
    /// <summary>
    /// Always succeeds from the caller's perspective (anti-enumeration).
    /// When the Auth user exists, enqueues a Rolvix-branded Resend email with a recovery link.
    /// </summary>
    Task RequestAsync(string email, CancellationToken cancellationToken);
}
