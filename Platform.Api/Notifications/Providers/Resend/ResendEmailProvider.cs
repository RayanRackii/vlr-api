using System.Net.Http.Headers;
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

    public async Task SendAsync(
        string recipient,
        string subject,
        string body,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            from = $"{_options.FromName} <{_options.FromEmail}>",
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
}
