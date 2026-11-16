using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Platform.Api.Modules.Payments.Gateways.Asaas;

/// <summary>
/// Asaas v3 adapter behind <see cref="IPaymentGateway"/>. The API key travels only in the <c>access_token</c> header of each
/// request. Failures surface as <see cref="PaymentGatewayException"/> carrying the Asaas error code and HTTP status, never
/// the request or response body.
/// </summary>
public sealed class AsaasGateway : IPaymentGateway
{
    public const string AccessTokenHeader = "access_token";
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    // Brazil has no daylight saving time since 2019, so a fixed offset is enough to pick the due date.
    private static readonly TimeSpan BrazilOffset = TimeSpan.FromHours(-3);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly IOptionsMonitor<AsaasOptions> _options;
    private readonly ILogger<AsaasGateway> _logger;

    public AsaasGateway(HttpClient http, IOptionsMonitor<AsaasOptions> options, ILogger<AsaasGateway> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public async Task<SubaccountResult> CreateSubaccountAsync(
        SubaccountRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rootKey = _options.CurrentValue.RootApiKey;
        if (string.IsNullOrWhiteSpace(rootKey))
        {
            throw new PaymentConfigurationException("Payments:Asaas:RootApiKey is not configured.");
        }

        var body = new AsaasCreateAccountBody(
            request.Name,
            request.Email,
            request.CpfCnpj,
            request.MobilePhone,
            request.PostalCode,
            request.Address,
            request.AddressNumber,
            request.Province,
            request.IncomeValue,
            request.CompanyType,
            request.Complement,
            request.BirthDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var response = await SendAsync<AsaasAccountResponse>(
            HttpMethod.Post, "accounts", rootKey, body, cancellationToken);

        if (string.IsNullOrEmpty(response.Id) || string.IsNullOrEmpty(response.WalletId) || string.IsNullOrEmpty(response.ApiKey))
        {
            throw new PaymentGatewayException("invalid_response", 200);
        }

        return new SubaccountResult(response.Id, response.WalletId, response.ApiKey, response.OnboardingUrl);
    }

    public async Task RegisterWebhookAsync(
        string apiKey,
        string url,
        string token,
        IReadOnlyCollection<string> events,
        CancellationToken cancellationToken = default)
    {
        var body = new AsaasWebhookBody(
            Name: "Rolvix",
            Url: url,
            Enabled: true,
            Interrupted: false,
            ApiVersion: 3,
            AuthToken: token,
            SendType: "NON_SEQUENTIALLY",
            Events: events);

        await SendAsync<JsonElement>(HttpMethod.Post, "webhooks", apiKey, body, cancellationToken);
    }

    public async Task<string> CreateCustomerAsync(
        string apiKey,
        CustomerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = new AsaasCustomerBody(request.Name, request.Email, request.Cpf, request.Phone, NotificationDisabled: true);
        var response = await SendAsync<AsaasIdResponse>(HttpMethod.Post, "customers", apiKey, body, cancellationToken);
        return string.IsNullOrEmpty(response.Id)
            ? throw new PaymentGatewayException("invalid_response", 200)
            : response.Id;
    }

    public async Task<ChargeResult> CreatePixChargeAsync(
        string apiKey,
        PixChargeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<AsaasSplitBody>? split = request.Split is { Percent: > 0 } s
            ? [new AsaasSplitBody(s.WalletId, s.Percent)]
            : null;

        var body = new AsaasPaymentBody(
            request.CustomerId,
            "PIX",
            request.Amount,
            request.DueAt.ToOffset(BrazilOffset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            request.Description,
            request.ExternalReference,
            split);

        var payment = await SendAsync<AsaasPaymentResponse>(HttpMethod.Post, "payments", apiKey, body, cancellationToken);
        if (string.IsNullOrEmpty(payment.Id))
        {
            throw new PaymentGatewayException("invalid_response", 200);
        }

        var qr = await SendAsync<AsaasPixQrCodeResponse>(
            HttpMethod.Get,
            $"payments/{Uri.EscapeDataString(payment.Id)}/pixQrCode",
            apiKey,
            null,
            cancellationToken);
        if (string.IsNullOrEmpty(qr.Payload) || string.IsNullOrEmpty(qr.EncodedImage))
        {
            throw new PaymentGatewayException("invalid_response", 200);
        }

        return new ChargeResult(payment.Id, qr.Payload, qr.EncodedImage, payment.InvoiceUrl, request.DueAt);
    }

    public async Task<GatewayCharge> GetChargeAsync(
        string apiKey,
        string externalId,
        CancellationToken cancellationToken = default)
    {
        var payment = await SendAsync<AsaasPaymentResponse>(
            HttpMethod.Get, $"payments/{Uri.EscapeDataString(externalId)}", apiKey, null, cancellationToken);
        return Map(payment);
    }

    public async Task<GatewayCharge?> FindChargeByExternalReferenceAsync(
        string apiKey,
        string reference,
        CancellationToken cancellationToken = default)
    {
        var list = await SendAsync<AsaasPaymentListResponse>(
            HttpMethod.Get,
            $"payments?externalReference={Uri.EscapeDataString(reference)}",
            apiKey,
            null,
            cancellationToken);

        var first = list.Data?.FirstOrDefault();
        return first is null ? null : Map(first);
    }

    public async Task<GatewayAccountStatus> GetAccountStatusAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        var status = await SendAsync<AsaasAccountStatusResponse>(
            HttpMethod.Get, "myAccount/status", apiKey, null, cancellationToken);

        string?[] parts = [status.General, status.CommercialInfo, status.BankAccountInfo, status.Documentation];
        if (parts.Any(p => string.Equals(p, "REJECTED", StringComparison.OrdinalIgnoreCase)))
        {
            return GatewayAccountStatus.Rejected;
        }

        var known = parts.Where(p => !string.IsNullOrEmpty(p)).ToArray();
        return known.Length > 0 && known.All(p => string.Equals(p, "APPROVED", StringComparison.OrdinalIgnoreCase))
            ? GatewayAccountStatus.Active
            : GatewayAccountStatus.Pending;
    }

    private static GatewayCharge Map(AsaasPaymentResponse payment)
    {
        if (string.IsNullOrEmpty(payment.Id))
        {
            throw new PaymentGatewayException("invalid_response", 200);
        }

        var status = (payment.Status ?? string.Empty).ToUpperInvariant() switch
        {
            "PENDING" or "AWAITING_RISK_ANALYSIS" => GatewayChargeStatus.Pending,
            "RECEIVED" or "CONFIRMED" or "RECEIVED_IN_CASH" => GatewayChargeStatus.Paid,
            "OVERDUE" => GatewayChargeStatus.Overdue,
            "REFUNDED" or "REFUND_REQUESTED" or "REFUND_IN_PROGRESS" => GatewayChargeStatus.Refunded,
            "DELETED" => GatewayChargeStatus.Deleted,
            _ => GatewayChargeStatus.Unknown,
        };

        DateOnly? paymentDate = DateOnly.TryParseExact(
            payment.PaymentDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

        return new GatewayCharge(
            payment.Id, status, payment.Value, payment.ExternalReference, payment.InvoiceUrl, paymentDate);
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string path,
        string apiKey,
        object? body,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new PaymentConfigurationException("A payment provider API key is required for this call.");
        }

        var baseUrl = _options.CurrentValue.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl.TrimEnd('/') + "/" + path, UriKind.Absolute, out var uri))
        {
            throw new PaymentConfigurationException("Payments:Asaas:BaseUrl is not configured or is not an absolute URL.");
        }

        using var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation(AccessTokenHeader, apiKey);
        request.Headers.Accept.ParseAdd("application/json");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Asaas {Method} {Path} timed out", method.Method, SafePath(path));
            throw new PaymentGatewayException("gateway_timeout", 0);
        }
        catch (HttpRequestException)
        {
            // The inner exception is dropped on purpose: it can echo request details.
            _logger.LogWarning("Asaas {Method} {Path} could not be reached", method.Method, SafePath(path));
            throw new PaymentGatewayException("gateway_unreachable", 0);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                var code = await ReadErrorCodeAsync(response, cancellationToken);
                _logger.LogWarning(
                    "Asaas {Method} {Path} failed with HTTP {Status} code {Code}",
                    method.Method, SafePath(path), status, code);
                throw new PaymentGatewayException(code, status);
            }

            try
            {
                var result = await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
                return result ?? throw new PaymentGatewayException("invalid_response", status);
            }
            catch (JsonException)
            {
                throw new PaymentGatewayException("invalid_response", status);
            }
        }
    }

    private static async Task<string> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            return "unexpected_redirect";
        }

        try
        {
            var error = await response.Content.ReadFromJsonAsync<AsaasErrorResponse>(Json, cancellationToken);
            var code = error?.Errors?.FirstOrDefault()?.Code;
            return IsSafeCode(code) ? code! : "unknown";
        }
        catch (JsonException)
        {
            return "unknown";
        }
    }

    // Only a short token made of letters, digits, '_', '-' and '.' is ever surfaced, so a stray body cannot leak through.
    private static bool IsSafeCode(string? code) =>
        !string.IsNullOrEmpty(code)
        && code.Length <= 64
        && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    // The query string is dropped from logs; it only carries references, but there is no reason to log it.
    private static string SafePath(string path)
    {
        var index = path.IndexOf('?', StringComparison.Ordinal);
        return index < 0 ? path : path[..index];
    }
}
