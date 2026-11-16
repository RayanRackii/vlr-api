using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Api.Modules.Payments;
using Platform.Api.Modules.Payments.Gateways;
using Platform.Api.Modules.Payments.Gateways.Asaas;

namespace Platform.Api.Tests.Payments;

public sealed class AsaasGatewayTests
{
    private const string RootKey = "$aact_root_SECRET_KEY_000";
    private const string SubKey = "$aact_sub_SECRET_KEY_111";
    private const string BaseUrl = "https://api-sandbox.asaas.com/v3";

    [Fact]
    public async Task CreateSubaccount_posts_to_accounts_with_the_root_key_and_maps_the_result()
    {
        var h = new FakeHandler(Json(HttpStatusCode.OK,
            """{"id":"acc_1","apiKey":"$aact_new_sub","walletId":"wal_1","onboardingUrl":"https://onboard"}"""));
        var (sut, _) = Create(h);

        var result = await sut.CreateSubaccountAsync(new SubaccountRequest(
            "Quadra Ltda", "a@b.com", "12345678000199", "11999999999", "01310100", "Av Paulista", "100", "Bela Vista",
            5000m, CompanyType: "LIMITED"));

        var call = Assert.Single(h.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal($"{BaseUrl}/accounts", call.Url);
        Assert.Equal(RootKey, call.AccessToken);
        using var body = JsonDocument.Parse(call.Body!);
        Assert.Equal("12345678000199", body.RootElement.GetProperty("cpfCnpj").GetString());
        Assert.Equal("LIMITED", body.RootElement.GetProperty("companyType").GetString());
        Assert.False(body.RootElement.TryGetProperty("birthDate", out _));
        Assert.Equal(new SubaccountResult("acc_1", "wal_1", "$aact_new_sub", "https://onboard"), result);
    }

    [Fact]
    public async Task CreateSubaccount_without_root_key_throws_configuration_exception_before_any_call()
    {
        var h = new FakeHandler(Json(HttpStatusCode.OK, "{}"));
        var (sut, _) = Create(h, rootKey: null);

        await Assert.ThrowsAsync<PaymentConfigurationException>(() => sut.CreateSubaccountAsync(
            new SubaccountRequest("n", "e@e.com", "1", "1", "1", "a", "1", "p", 1m)));
        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task RegisterWebhook_posts_events_and_token_with_the_subaccount_key()
    {
        var h = new FakeHandler(Json(HttpStatusCode.OK, """{"id":"wh_1"}"""));
        var (sut, _) = Create(h);

        await sut.RegisterWebhookAsync(SubKey, "https://api.example/api/webhooks/payments/asaas", "tok-123456789012345678901234567890123",
            PaymentWebhookEvents.Default);

        var call = Assert.Single(h.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal($"{BaseUrl}/webhooks", call.Url);
        Assert.Equal(SubKey, call.AccessToken);
        using var body = JsonDocument.Parse(call.Body!);
        Assert.Equal("https://api.example/api/webhooks/payments/asaas", body.RootElement.GetProperty("url").GetString());
        Assert.Equal("tok-123456789012345678901234567890123", body.RootElement.GetProperty("authToken").GetString());
        Assert.Equal(3, body.RootElement.GetProperty("apiVersion").GetInt32());
        var events = body.RootElement.GetProperty("events").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(
            ["PAYMENT_RECEIVED", "PAYMENT_CONFIRMED", "PAYMENT_OVERDUE", "PAYMENT_DELETED", "PAYMENT_REFUNDED"], events);
    }

    [Fact]
    public async Task CreateCustomer_posts_to_customers_and_returns_the_id()
    {
        var h = new FakeHandler(Json(HttpStatusCode.OK, """{"id":"cus_1"}"""));
        var (sut, _) = Create(h);

        var id = await sut.CreateCustomerAsync(SubKey, new CustomerRequest("Ana", "ana@x.com", "52998224725", "11988887777"));

        Assert.Equal("cus_1", id);
        var call = Assert.Single(h.Calls);
        Assert.Equal($"{BaseUrl}/customers", call.Url);
        Assert.Equal(SubKey, call.AccessToken);
        using var body = JsonDocument.Parse(call.Body!);
        Assert.Equal("52998224725", body.RootElement.GetProperty("cpfCnpj").GetString());
        Assert.Equal("11988887777", body.RootElement.GetProperty("mobilePhone").GetString());
    }

    [Fact]
    public async Task CreatePixCharge_creates_a_PIX_payment_then_reads_the_qr_code()
    {
        var h = new FakeHandler(
            Json(HttpStatusCode.OK, """{"id":"pay_1","status":"PENDING","value":50.5,"invoiceUrl":"https://inv"}"""),
            Json(HttpStatusCode.OK, """{"encodedImage":"QUJD","payload":"000201..."}"""));
        var (sut, _) = Create(h);
        var dueAt = new DateTimeOffset(2026, 11, 16, 1, 0, 0, TimeSpan.Zero); // 22:00 on the 15th in Brazil

        var result = await sut.CreatePixChargeAsync(SubKey, new PixChargeRequest(
            "cus_1", 50.5m, dueAt, "Sinal da reserva", "reservation:abc", new ChargeSplit("wal_platform", 5m)));

        Assert.Equal(2, h.Calls.Count);
        Assert.Equal(HttpMethod.Post, h.Calls[0].Method);
        Assert.Equal($"{BaseUrl}/payments", h.Calls[0].Url);
        using var body = JsonDocument.Parse(h.Calls[0].Body!);
        Assert.Equal("PIX", body.RootElement.GetProperty("billingType").GetString());
        Assert.Equal("cus_1", body.RootElement.GetProperty("customer").GetString());
        Assert.Equal(50.5m, body.RootElement.GetProperty("value").GetDecimal());
        Assert.Equal("2026-11-15", body.RootElement.GetProperty("dueDate").GetString());
        Assert.Equal("reservation:abc", body.RootElement.GetProperty("externalReference").GetString());
        var split = Assert.Single(body.RootElement.GetProperty("split").EnumerateArray());
        Assert.Equal("wal_platform", split.GetProperty("walletId").GetString());
        Assert.Equal(5m, split.GetProperty("percentualValue").GetDecimal());

        Assert.Equal(HttpMethod.Get, h.Calls[1].Method);
        Assert.Equal($"{BaseUrl}/payments/pay_1/pixQrCode", h.Calls[1].Url);
        Assert.All(h.Calls, c => Assert.Equal(SubKey, c.AccessToken));

        Assert.Equal(new ChargeResult("pay_1", "000201...", "QUJD", "https://inv", dueAt), result);
    }

    [Fact]
    public async Task CreatePixCharge_without_split_or_with_zero_percent_sends_no_split()
    {
        var h = new FakeHandler(
            Json(HttpStatusCode.OK, """{"id":"pay_1"}"""),
            Json(HttpStatusCode.OK, """{"encodedImage":"QUJD","payload":"p"}"""));
        var (sut, _) = Create(h);

        await sut.CreatePixChargeAsync(SubKey, new PixChargeRequest(
            "cus_1", 10m, DateTimeOffset.UtcNow, "d", "ref", new ChargeSplit("w", 0m)));

        using var body = JsonDocument.Parse(h.Calls[0].Body!);
        Assert.False(body.RootElement.TryGetProperty("split", out _));
    }

    [Fact]
    public async Task GetCharge_gets_the_payment_and_maps_the_status()
    {
        var h = new FakeHandler(Json(HttpStatusCode.OK,
            """{"id":"pay_1","status":"RECEIVED","value":20,"externalReference":"r1","paymentDate":"2026-11-15"}"""));
        var (sut, _) = Create(h);

        var charge = await sut.GetChargeAsync(SubKey, "pay_1");

        var call = Assert.Single(h.Calls);
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Equal($"{BaseUrl}/payments/pay_1", call.Url);
        Assert.Equal(SubKey, call.AccessToken);
        Assert.Equal(GatewayChargeStatus.Paid, charge.Status);
        Assert.Equal(20m, charge.Amount);
        Assert.Equal(new DateOnly(2026, 11, 15), charge.PaymentDate);
    }

    [Fact]
    public async Task FindChargeByExternalReference_queries_by_reference_and_returns_first_or_null()
    {
        var h = new FakeHandler(
            Json(HttpStatusCode.OK, """{"data":[{"id":"pay_9","status":"OVERDUE","value":1,"externalReference":"a b"}]}"""),
            Json(HttpStatusCode.OK, """{"data":[]}"""));
        var (sut, _) = Create(h);

        var found = await sut.FindChargeByExternalReferenceAsync(SubKey, "a b");
        var missing = await sut.FindChargeByExternalReferenceAsync(SubKey, "none");

        Assert.Equal($"{BaseUrl}/payments?externalReference=a%20b", h.Calls[0].Url);
        Assert.Equal(HttpMethod.Get, h.Calls[0].Method);
        Assert.Equal("pay_9", found!.ExternalId);
        Assert.Equal(GatewayChargeStatus.Overdue, found.Status);
        Assert.Null(missing);
    }

    [Theory]
    [InlineData("""{"general":"APPROVED","commercialInfo":"APPROVED","bankAccountInfo":"APPROVED","documentation":"APPROVED"}""", GatewayAccountStatus.Active)]
    [InlineData("""{"general":"PENDING","commercialInfo":"APPROVED","bankAccountInfo":"APPROVED","documentation":"PENDING"}""", GatewayAccountStatus.Pending)]
    [InlineData("""{"general":"APPROVED","commercialInfo":"APPROVED","bankAccountInfo":"REJECTED","documentation":"APPROVED"}""", GatewayAccountStatus.Rejected)]
    public async Task GetAccountStatus_maps_to_pending_active_or_rejected(string json, GatewayAccountStatus expected)
    {
        var h = new FakeHandler(Json(HttpStatusCode.OK, json));
        var (sut, _) = Create(h);

        var status = await sut.GetAccountStatusAsync(SubKey);

        Assert.Equal(expected, status);
        var call = Assert.Single(h.Calls);
        Assert.Equal($"{BaseUrl}/myAccount/status", call.Url);
        Assert.Equal(SubKey, call.AccessToken);
    }

    [Fact]
    public async Task Non_2xx_maps_to_code_and_status_and_hides_the_body_and_the_keys()
    {
        const string leakyBody =
            """{"errors":[{"code":"invalid_customer","description":"token $aact_sub_SECRET_KEY_111 CPF 52998224725 rejected"}]}""";
        var h = new FakeHandler(Json(HttpStatusCode.BadRequest, leakyBody));
        var (sut, logs) = Create(h);

        var ex = await Assert.ThrowsAsync<PaymentGatewayException>(() => sut.GetChargeAsync(SubKey, "pay_1"));

        Assert.Equal("invalid_customer", ex.Code);
        Assert.Equal(400, ex.HttpStatus);
        AssertNoSecrets(ex.ToString(), logs);
        Assert.DoesNotContain("description", ex.ToString());
        Assert.DoesNotContain("52998224725", ex.ToString());
        Assert.NotEmpty(logs.Messages);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"errors":[{"code":"has spaces and $aact_sub_SECRET_KEY_111"}]}""")]
    [InlineData("""{"errors":[]}""")]
    public async Task Unusable_error_bodies_yield_the_unknown_code(string body)
    {
        var h = new FakeHandler(Json(HttpStatusCode.InternalServerError, body));
        var (sut, logs) = Create(h);

        var ex = await Assert.ThrowsAsync<PaymentGatewayException>(() => sut.GetChargeAsync(SubKey, "pay_1"));

        Assert.Equal("unknown", ex.Code);
        Assert.Equal(500, ex.HttpStatus);
        AssertNoSecrets(ex.ToString(), logs);
    }

    [Fact]
    public async Task Redirect_response_is_an_error_and_is_not_followed()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.Found);
        redirect.Headers.Location = new Uri("https://evil.example/steal");
        var h = new FakeHandler(redirect);
        var (sut, logs) = Create(h);

        var ex = await Assert.ThrowsAsync<PaymentGatewayException>(() => sut.GetChargeAsync(SubKey, "pay_1"));

        Assert.Equal("unexpected_redirect", ex.Code);
        Assert.Equal(302, ex.HttpStatus);
        Assert.Single(h.Calls);
        AssertNoSecrets(ex.ToString(), logs);
    }

    [Fact]
    public async Task Transport_failure_maps_to_gateway_unreachable_without_inner_details()
    {
        var h = new FakeHandler(new HttpRequestException($"boom with {SubKey}"));
        var (sut, logs) = Create(h);

        var ex = await Assert.ThrowsAsync<PaymentGatewayException>(() => sut.GetChargeAsync(SubKey, "pay_1"));

        Assert.Equal("gateway_unreachable", ex.Code);
        Assert.Equal(0, ex.HttpStatus);
        Assert.Null(ex.InnerException);
        AssertNoSecrets(ex.ToString(), logs);
    }

    [Fact]
    public async Task Timeout_maps_to_gateway_timeout()
    {
        var h = new FakeHandler(new TaskCanceledException("timed out"));
        var (sut, _) = Create(h);

        var ex = await Assert.ThrowsAsync<PaymentGatewayException>(() => sut.GetChargeAsync(SubKey, "pay_1"));

        Assert.Equal("gateway_timeout", ex.Code);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_wrapped()
    {
        var h = new FakeHandler(new TaskCanceledException("cancelled"));
        var (sut, _) = Create(h);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.GetChargeAsync(SubKey, "pay_1", cts.Token));
    }

    [Fact]
    public async Task Missing_base_url_throws_configuration_exception_at_use()
    {
        var h = new FakeHandler(Json(HttpStatusCode.OK, "{}"));
        var (sut, _) = Create(h, baseUrl: null);

        await Assert.ThrowsAsync<PaymentConfigurationException>(() => sut.GetChargeAsync(SubKey, "pay_1"));
        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task Base_url_with_trailing_slash_builds_the_same_path()
    {
        var h = new FakeHandler(Json(HttpStatusCode.OK, """{"id":"pay_1","status":"PENDING","value":1}"""));
        var (sut, _) = Create(h, baseUrl: BaseUrl + "/");

        await sut.GetChargeAsync(SubKey, "pay_1");

        Assert.Equal($"{BaseUrl}/payments/pay_1", h.Calls[0].Url);
    }

    [Fact]
    public async Task Successful_calls_log_nothing_secret()
    {
        var h = new FakeHandler(Json(HttpStatusCode.OK, """{"id":"pay_1","status":"PENDING","value":1}"""));
        var (sut, logs) = Create(h);

        await sut.GetChargeAsync(SubKey, "pay_1");

        AssertNoSecrets(string.Empty, logs);
    }

    private static void AssertNoSecrets(string exceptionText, CapturingLogger logs)
    {
        foreach (var secret in new[] { SubKey, RootKey })
        {
            Assert.DoesNotContain(secret, exceptionText);
            Assert.DoesNotContain(logs.Messages, m => m.Contains(secret, StringComparison.Ordinal));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (AsaasGateway Sut, CapturingLogger Logs) Create(
        FakeHandler handler,
        string? baseUrl = BaseUrl,
        string? rootKey = RootKey)
    {
        var logs = new CapturingLogger();
        var options = new AsaasOptions { BaseUrl = baseUrl, RootApiKey = rootKey, PlatformWalletId = "wal_platform" };
        var sut = new AsaasGateway(new HttpClient(handler), new StaticMonitor(options), logs);
        return (sut, logs);
    }

    private sealed record Call(HttpMethod Method, string Url, string? AccessToken, string? Body);

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<object> _queue = new();

        public FakeHandler(params object[] responsesOrExceptions)
        {
            foreach (var item in responsesOrExceptions)
            {
                _queue.Enqueue(item);
            }
        }

        public List<Call> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = request.Headers.TryGetValues("access_token", out var values) ? values.Single() : null;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add(new Call(request.Method, request.RequestUri!.OriginalString, token, body));

            var next = _queue.Count > 0 ? _queue.Dequeue() : throw new InvalidOperationException("No response queued.");
            if (next is Exception ex)
            {
                throw ex;
            }

            return (HttpResponseMessage)next;
        }
    }

    private sealed class StaticMonitor(AsaasOptions value) : IOptionsMonitor<AsaasOptions>
    {
        public AsaasOptions CurrentValue => value;

        public AsaasOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<AsaasOptions, string?> listener) => null;
    }

    private sealed class CapturingLogger : ILogger<AsaasGateway>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception is not null)
            {
                Messages.Add(exception.ToString());
            }
        }
    }
}
