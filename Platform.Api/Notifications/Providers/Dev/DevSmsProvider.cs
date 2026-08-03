namespace Platform.Api.Notifications.Providers.Dev;

public sealed class DevSmsProvider(ILogger<DevSmsProvider> logger) : ISmsProvider
{
    public Task SendAsync(
        string recipient,
        string body,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "DEV SMS → To: {Recipient} | Body: {Body}",
            recipient,
            body);

        return Task.CompletedTask;
    }
}
