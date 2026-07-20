namespace Platform.Api.Notifications;

public sealed class DevWhatsAppProvider(ILogger<DevWhatsAppProvider> logger) : IWhatsAppProvider
{
    public Task SendAsync(
        string recipient,
        string body,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "DEV WHATSAPP → To: {Recipient} | Body: {Body}",
            recipient,
            body);

        return Task.CompletedTask;
    }
}
