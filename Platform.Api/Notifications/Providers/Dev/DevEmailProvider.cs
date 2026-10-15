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
        logger.LogInformation(
            "DEV EMAIL → To: {Recipient} | FromName: {FromName} | Subject: {Subject} | Body: {Body}",
            recipient,
            string.IsNullOrWhiteSpace(fromDisplayName) ? "(provider default)" : fromDisplayName.Trim(),
            subject,
            body);

        return Task.CompletedTask;
    }
}
