namespace Platform.Api.Notifications;

public interface IEmailProvider
{
    Task SendAsync(
        string recipient,
        string subject,
        EmailContent content,
        CancellationToken cancellationToken = default)
        => SendAsync(recipient, subject, content, null, cancellationToken);

    Task SendAsync(
        string recipient,
        string subject,
        EmailContent content,
        string? fromDisplayName,
        CancellationToken cancellationToken)
        => SendAsync(recipient, subject, content.Html, fromDisplayName, cancellationToken);

    /// <summary>Plain text entry point. Providers encode it and apply the shared HTML layout.</summary>
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
