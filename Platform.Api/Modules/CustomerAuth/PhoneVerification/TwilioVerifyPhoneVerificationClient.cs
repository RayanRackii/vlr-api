using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Platform.Api.Modules.CustomerAuth.PhoneVerification;

public sealed class TwilioVerifyPhoneVerificationClient : IPhoneVerificationClient
{
    public const string ProviderUnavailableMessage =
        "Phone verification provider unavailable. Try again.";

    public const string RateLimitedMessage =
        "Too many verification attempts. Try again shortly.";

    public const string InvalidOrExpiredMessage =
        "Invalid or expired verification code.";

    public const string CanceledMessage =
        "Verification canceled. Please request a new code.";

    private const int TwilioInvalidParameter = 60200;
    private const int TwilioMaxCheckAttempts = 60202;
    private const int TwilioMaxSendAttempts = 60203;
    private const int TwilioInvalidCode = 60223;

    private readonly HttpClient _httpClient;
    private readonly TwilioVerifyOptions _options;
    private readonly ILogger<TwilioVerifyPhoneVerificationClient> _logger;

    public TwilioVerifyPhoneVerificationClient(
        HttpClient httpClient,
        IOptions<TwilioVerifyOptions> options,
        ILogger<TwilioVerifyPhoneVerificationClient> logger)
    {
        _options = options.Value;
        _logger = logger;

        var baseUrl = string.IsNullOrWhiteSpace(_options.BaseUrl)
            ? "https://verify.twilio.com/v2/"
            : _options.BaseUrl.TrimEnd('/') + "/";
        httpClient.BaseAddress = new Uri(baseUrl);
        _httpClient = httpClient;
    }

    public Task StartVerificationAsync(string phoneE164, CancellationToken cancellationToken) =>
        SendAsync(
            form: new Dictionary<string, string>
            {
                ["To"] = phoneE164,
                ["Channel"] = "sms",
            },
            phoneE164,
            isCheck: false,
            cancellationToken);

    public Task CheckVerificationAsync(
        string phoneE164,
        string code,
        CancellationToken cancellationToken) =>
        SendAsync(
            form: new Dictionary<string, string>
            {
                ["To"] = phoneE164,
                ["Code"] = code,
            },
            phoneE164,
            isCheck: true,
            cancellationToken);

    private async Task SendAsync(
        Dictionary<string, string> form,
        string phoneE164,
        bool isCheck,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();

        var relativePath = isCheck
            ? $"Services/{_options.VerifyServiceSid}/VerificationCheck"
            : $"Services/{_options.VerifyServiceSid}/Verifications";


        var last4 = PhoneLast4(phoneE164);
        using var request = new HttpRequestMessage(HttpMethod.Post, relativePath)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", BuildBasicToken());

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Phone verification timed out for phone ending {Last4}.",
                last4);
            throw new PhoneVerificationProviderException(ProviderUnavailableMessage, ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex,
                "Phone verification request failed for phone ending {Last4}.",
                last4);
            throw new PhoneVerificationProviderException(ProviderUnavailableMessage, ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            TryParseTwilioBody(body, out var verificationStatus, out var twilioCode);

            if (!IsSuccess(response.StatusCode))
            {
                _logger.LogWarning(
                    "Phone verification {Operation} for phone ending {Last4} failed with HTTP {StatusCode} Twilio code {TwilioCode}.",
                    isCheck ? "check" : "start",
                    last4,
                    (int)response.StatusCode,
                    twilioCode);
            }
            else
            {
                _logger.LogInformation(
                    "Phone verification {Operation} for phone ending {Last4} returned HTTP {StatusCode} status {VerificationStatus}.",
                    isCheck ? "check" : "start",
                    last4,
                    (int)response.StatusCode,
                    verificationStatus);
            }

            MapOutcome(response.StatusCode, verificationStatus, twilioCode, isCheck);
        }
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKeySid)
            || string.IsNullOrWhiteSpace(_options.ApiKeySecret)
            || string.IsNullOrWhiteSpace(_options.VerifyServiceSid))
        {
            throw new PhoneVerificationProviderException(ProviderUnavailableMessage);
        }
    }

    private string BuildBasicToken() =>
        Convert.ToBase64String(
            Encoding.ASCII.GetBytes($"{_options.ApiKeySid}:{_options.ApiKeySecret}"));

    private static void MapOutcome(
        HttpStatusCode httpStatus,
        string? verificationStatus,
        int? twilioCode,
        bool isCheck)
    {
        if (httpStatus == HttpStatusCode.TooManyRequests
            || twilioCode is TwilioMaxCheckAttempts or TwilioMaxSendAttempts)
        {
            throw new PhoneVerificationRateLimitedException(RateLimitedMessage);
        }

        if (httpStatus is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new PhoneVerificationProviderException(ProviderUnavailableMessage);
        }

        if (httpStatus == HttpStatusCode.NotFound
            || twilioCode is TwilioInvalidParameter or TwilioInvalidCode)
        {
            throw new PhoneVerificationInvalidException(InvalidOrExpiredMessage);
        }

        var httpCode = (int)httpStatus;
        if (httpCode >= 500 || !IsSuccess(httpStatus))
        {
            throw new PhoneVerificationProviderException(ProviderUnavailableMessage);
        }

        if (string.Equals(verificationStatus, "canceled", StringComparison.OrdinalIgnoreCase))
        {
            throw new PhoneVerificationInvalidException(CanceledMessage);
        }

        if (isCheck)
        {
            if (!string.Equals(verificationStatus, "approved", StringComparison.OrdinalIgnoreCase))
            {
                throw new PhoneVerificationInvalidException(InvalidOrExpiredMessage);
            }

            return;
        }

        if (string.Equals(verificationStatus, "pending", StringComparison.OrdinalIgnoreCase)
            || string.Equals(verificationStatus, "approved", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new PhoneVerificationProviderException(ProviderUnavailableMessage);
    }

    private static bool IsSuccess(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code is >= 200 and <= 299;
    }

    private static void TryParseTwilioBody(
        string body,
        out string? verificationStatus,
        out int? twilioCode)
    {
        verificationStatus = null;
        twilioCode = null;
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.TryGetProperty("status", out var statusElement)
                && statusElement.ValueKind == JsonValueKind.String)
            {
                verificationStatus = statusElement.GetString();
            }

            // Numeric `status` is the HTTP code in Twilio error JSON, not a verification state.

            if (root.TryGetProperty("code", out var codeElement))
            {
                if (codeElement.ValueKind == JsonValueKind.Number
                    && codeElement.TryGetInt32(out var parsedCode))
                {
                    twilioCode = parsedCode;
                }
                else if (codeElement.ValueKind == JsonValueKind.String
                         && int.TryParse(codeElement.GetString(), out var parsedFromString))
                {
                    twilioCode = parsedFromString;
                }
            }
        }
        catch (JsonException)
        {
            // HTTP status remains the authority when the body is not JSON.
        }
    }

    private static string PhoneLast4(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length <= 4 ? digits : digits[^4..];
    }
}
