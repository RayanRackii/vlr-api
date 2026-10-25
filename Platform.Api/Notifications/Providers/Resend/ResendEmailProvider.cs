using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Platform.Api.Notifications.Providers.Resend;

public sealed class ResendEmailProvider : IEmailProvider
{
    private readonly HttpClient _httpClient;
    private readonly ResendOptions _options;
    private readonly ILogger<ResendEmailProvider> _logger;

    public ResendEmailProvider(
        HttpClient httpClient,
        IOptions<ResendOptions> options,
        ILogger<ResendEmailProvider> logger)
    {
        _options = options.Value;
        _logger = logger;

        httpClient.BaseAddress = new Uri(_options.ApiUrl.TrimEnd('/') + "/");
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        _httpClient = httpClient;
    }

    public Task SendAsync(
        string recipient,
        string subject,
        string body,
        CancellationToken cancellationToken = default) =>
        SendAsync(recipient, subject, body, fromDisplayName: null, cancellationToken);

    public async Task SendAsync(
        string recipient,
        string subject,
        string body,
        string? fromDisplayName,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            from = EmailFromHeader.Format(fromDisplayName, _options.FromEmail, _options.FromName),
            to = new[] { recipient },
            subject,
            html = body,
        };

        using var response = await _httpClient.PostAsJsonAsync(
            "emails",
            payload,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests && IsDailyQuotaExceeded(error))
            {
                // The outbox owns scheduling and emits the single, sanitized warning.
                throw new EmailDeferredException();
            }

            _logger.LogError(
                "Resend rejected email to {Recipient}. Status: {Status}. Response: {Response}",
                recipient,
                (int)response.StatusCode,
                error);

            throw new HttpRequestException(
                $"Resend returned {(int)response.StatusCode} when sending email to {recipient}.");
        }

        _logger.LogInformation("Email sent to {Recipient} via Resend.", recipient);
    }

    private static bool IsDailyQuotaExceeded(string error)
    {
        try
        {
            using var document = JsonDocument.Parse(error);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                && name.GetString() == "daily_quota_exceeded";
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
