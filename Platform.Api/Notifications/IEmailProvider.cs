namespace Platform.Api.Notifications;

public interface IEmailProvider
{
    Task SendAsync(
        string recipient,
        string subject,
        string body,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Same send as the four-argument method. <paramref name="fromDisplayName"/>
    /// replaces only the visible sender name. The mailbox stays the provider configuration.
    /// </summary>
    Task SendAsync(
        string recipient,
        string subject,
        string body,
        string? fromDisplayName,
        CancellationToken cancellationToken)
        => SendAsync(recipient, subject, body, cancellationToken);
}
