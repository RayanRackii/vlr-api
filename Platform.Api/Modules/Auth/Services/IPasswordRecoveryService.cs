namespace Platform.Api.Modules.Auth.Services;

public interface IPasswordRecoveryService
{
    /// <summary>
    /// Always completes without telling the caller whether the email exists.
    /// Sends a Rolvix recovery email only when the address is a user of the current tenant
    /// and an Auth account exists. No current tenant means no lookup and no email.
    /// </summary>
    Task RequestAsync(string email, CancellationToken cancellationToken);
}
