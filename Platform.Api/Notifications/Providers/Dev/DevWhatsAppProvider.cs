namespace Platform.Api.Notifications.Providers.Dev;

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

    public Task SendTemplateAsync(
        string recipient,
        string templateName,
        string languageCode,
        IReadOnlyList<string> bodyParameters,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "DEV WHATSAPP TEMPLATE → To: {Recipient} | Template: {Template} ({Language}) | Params: {Params}",
            recipient,
            templateName,
            languageCode,
            string.Join(", ", bodyParameters));

        return Task.CompletedTask;
    }
}
