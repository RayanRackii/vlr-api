using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace Platform.Api.Notifications.Providers.Meta;

public sealed class MetaWhatsAppProvider : IWhatsAppProvider
{
    private readonly HttpClient _httpClient;
    private readonly MetaWhatsAppOptions _options;
    private readonly ILogger<MetaWhatsAppProvider> _logger;

    public MetaWhatsAppProvider(
        HttpClient httpClient,
        IOptions<MetaWhatsAppOptions> options,
        ILogger<MetaWhatsAppProvider> logger)
    {
        _options = options.Value;
        _logger = logger;

        httpClient.BaseAddress = new Uri(_options.GraphApiUrl.TrimEnd('/') + "/");
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _options.AccessToken);
        _httpClient = httpClient;
    }

    public Task SendAsync(
        string recipient,
        string body,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = NormalizeRecipient(recipient),
            type = "text",
            text = new { preview_url = false, body },
        };

        return PostMessageAsync(recipient, payload, cancellationToken);
    }

    public Task SendTemplateAsync(
        string recipient,
        string templateName,
        string languageCode,
        IReadOnlyList<string> bodyParameters,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = NormalizeRecipient(recipient),
            type = "template",
            template = new
            {
                name = templateName,
                language = new { code = languageCode },
                components = BuildTemplateComponents(bodyParameters),
            },
        };

        return PostMessageAsync(recipient, payload, cancellationToken);
    }

    private static object[] BuildTemplateComponents(IReadOnlyList<string> bodyParameters)
    {
        if (bodyParameters.Count == 0)
        {
            return [];
        }

        var parameters = bodyParameters
            .Select(value => new { type = "text", text = value })
            .ToArray();

        return [new { type = "body", parameters }];
    }

    private async Task PostMessageAsync(
        string recipient,
        object payload,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            $"{_options.PhoneNumberId}/messages",
            payload,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);

            _logger.LogError(
                "Meta rejected WhatsApp message to {Recipient}. Status: {Status}. Response: {Response}",
                recipient,
                (int)response.StatusCode,
                error);

            throw new HttpRequestException(
                $"Meta Graph API returned {(int)response.StatusCode} when sending WhatsApp message to {recipient}.");
        }

        _logger.LogInformation("WhatsApp message sent to {Recipient} via Meta Cloud API.", recipient);
    }

    /// <summary>Graph API espera o número em formato E.164 sem "+" (ex.: 5511999998888).</summary>
    private static string NormalizeRecipient(string recipient)
    {
        return new string(recipient.Where(char.IsDigit).ToArray());
    }
}
