namespace Platform.Api.Notifications;

public sealed class DevEmailProvider(ILogger<DevEmailProvider> logger) : IEmailProvider
{
    public Task SendAsync(
        string recipient,
        string subject,
        string body,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "DEV EMAIL → To: {Recipient} | Subject: {Subject} | Body: {Body}",
            recipient,
            subject,
            body);

        return Task.CompletedTask;
    }
}
