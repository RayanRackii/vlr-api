using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Platform.Api.Notifications.Providers.Meta;

public sealed class MetaWhatsAppProvider : IWhatsAppProvider
{
    private static readonly HashSet<int> PermanentErrorCodes =
    [
        100, 131026, 132000, 132001, 132005, 132007, 132012, 132015, 132016, 133010,
    ];

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

    public Task<string?> SendTemplateAsync(
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

    private async Task<string?> PostMessageAsync(
        string recipient,
        object payload,
        CancellationToken cancellationToken)
    {
        var last4 = PhoneLogMask.Last4(recipient);
        using var response = await _httpClient.PostAsJsonAsync(
            $"{_options.PhoneNumberId}/messages",
            payload,
            cancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            TryParseError(responseBody, out var providerCode, out var providerTitle);
            var isTransient = IsTransientStatus(response.StatusCode, providerCode);

            _logger.LogError(
                "Meta rejected WhatsApp message to phone ending {Last4}. Status: {Status}. Code: {Code}. Title: {Title}.",
                last4,
                (int)response.StatusCode,
                providerCode,
                providerTitle);

            throw new WhatsAppSendException(
                $"Meta Graph API returned {(int)response.StatusCode} (code {providerCode}) when sending WhatsApp to phone ending {last4}.",
                isTransient,
                providerCode);
        }

        var messageId = TryParseMessageId(responseBody);
        _logger.LogInformation(
            "WhatsApp message sent to phone ending {Last4} via Meta Cloud API. MessageId present: {HasId}.",
            last4,
            !string.IsNullOrWhiteSpace(messageId));

        return messageId;
    }

    private static bool IsTransientStatus(HttpStatusCode statusCode, int? providerCode)
    {
        var code = (int)statusCode;
        if (code == 429 || code >= 500)
        {
            return true;
        }

        if (providerCode is { } parsed && PermanentErrorCodes.Contains(parsed))
        {
            return false;
        }

        return code is >= 400 and < 500 ? false : true;
    }

    private static void TryParseError(string body, out int? code, out string? title)
    {
        code = null;
        title = null;
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("error", out var error))
            {
                return;
            }

            if (error.TryGetProperty("code", out var codeElement)
                && codeElement.TryGetInt32(out var parsed))
            {
                code = parsed;
            }

            if (error.TryGetProperty("error_user_title", out var titleElement)
                && titleElement.ValueKind == JsonValueKind.String)
            {
                title = titleElement.GetString();
            }
            else if (error.TryGetProperty("type", out var typeElement)
                     && typeElement.ValueKind == JsonValueKind.String)
            {
                title = typeElement.GetString();
            }
            else if (error.TryGetProperty("message", out var messageElement)
                     && messageElement.ValueKind == JsonValueKind.String)
            {
                var message = messageElement.GetString();
                title = message is { Length: > 80 } ? message[..80] : message;
            }
        }
        catch (JsonException)
        {
        }
    }

    private static string? TryParseMessageId(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("messages", out var messages)
                || messages.ValueKind != JsonValueKind.Array
                || messages.GetArrayLength() == 0)
            {
                return null;
            }

            var first = messages[0];
            return first.TryGetProperty("id", out var idElement)
                ? idElement.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Graph API espera o número em formato E.164 sem "+" (ex.: 5511999998888).</summary>
    internal static string NormalizeRecipient(string recipient)
    {
        return new string(recipient.Where(char.IsDigit).ToArray());
    }
}
