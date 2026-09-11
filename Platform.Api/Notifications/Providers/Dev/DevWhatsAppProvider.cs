namespace Platform.Api.Notifications.Providers.Dev;

public sealed class DevWhatsAppProvider(ILogger<DevWhatsAppProvider> logger) : IWhatsAppProvider
{
    public Task SendAsync(
        string recipient,
        string body,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "DEV WHATSAPP → To phone ending {Last4} | Body length: {Length}",
            PhoneLogMask.Last4(recipient),
            body.Length);

        return Task.CompletedTask;
    }

    public Task<string?> SendTemplateAsync(
        string recipient,
        string templateName,
        string languageCode,
        IReadOnlyList<string> bodyParameters,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "DEV WHATSAPP TEMPLATE → To phone ending {Last4} | Template: {Template} ({Language}) | ParamCount: {ParamCount}",
            PhoneLogMask.Last4(recipient),
            templateName,
            languageCode,
            bodyParameters.Count);

        return Task.FromResult<string?>("dev-whatsapp");
    }
}
