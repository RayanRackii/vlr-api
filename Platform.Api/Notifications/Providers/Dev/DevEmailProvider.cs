namespace Platform.Api.Notifications.Providers.Dev;

public sealed class DevEmailProvider(ILogger<DevEmailProvider> logger) : IEmailProvider
{
    public Task SendAsync(
        string recipient,
        string subject,
        string body,
        CancellationToken cancellationToken = default) =>
        SendAsync(recipient, subject, body, fromDisplayName: null, cancellationToken);

    public Task SendAsync(
        string recipient,
        string subject,
        string body,
        string? fromDisplayName,
        CancellationToken cancellationToken)
    {
        // Production uses this provider when Resend is off. The HTML body and the
        // display name can carry customer data, prices, portal links, and invite tokens.
        logger.LogInformation(
            "DEV EMAIL → To: {Recipient} | Subject: {Subject}",
            EmailHeaderText.StripControls(recipient),
            EmailHeaderText.StripControls(subject));

        return Task.CompletedTask;
    }
}
