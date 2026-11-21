using System.Net;
using System.Text;
using System.Text.Json;
using Platform.Api.Modules.Payments;
using Platform.Api.Modules.Payments.Gateways;
using Platform.Api.Modules.Payments.Gateways.Asaas;
using Platform.Api.Tests.Billing;

namespace Platform.Api.Tests.Payments;

/// <summary>The platform billing (root account) side of the Asaas adapter, against a fake HTTP handler.</summary>
public sealed class AsaasBillingGatewayTests
{
    private const string RootKey = "$aact_root_SECRET_KEY_BILLING_111";
    private const string BaseUrl = "https://api-sandbox.asaas.com/v3";

    [Fact]
    public async Task CreateOrGetCustomer_returns_the_existing_customer_without_creating_one()
    {
        var h = new FakeHandler(Json(HttpStatusCode.OK, """{"data":[{"id":"cus_existing"}]}"""));
        var (sut, _) = Create(h);

        var id = await sut.CreateOrGetCustomerAsync(Customer());

        Assert.Equal("cus_existing", id);
        var call = Assert.Single(h.Calls);
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Equal($"{BaseUrl}/customers?externalReference=tenant-1", call.Url);
        Assert.Equal(RootKey, call.AccessToken);
    }

    [Fact]
    public async Task CreateOrGetCustomer_creates_it_with_the_tenant_as_external_reference_when_none_exists()
    {
        var h = new FakeHandler(
            Json(HttpStatusCode.OK, """{"data":[]}"""),
            Json(HttpStatusCode.OK, """{"id":"cus_new"}"""));
        var (sut, _) = Create(h);

        var id = await sut.CreateOrGetCustomerAsync(Customer());

        Assert.Equal("cus_new", id);
        Assert.Equal(2, h.Calls.Count);
        var create = h.Calls[1];
        Assert.Equal(HttpMethod.Post, create.Method);
        Assert.Equal($"{BaseUrl}/customers", create.Url);
        Assert.Equal(RootKey, create.AccessToken);
        using var body = JsonDocument.Parse(create.Body!);
        Assert.Equal("tenant-1", body.RootElement.GetProperty("externalReference").GetString());
        Assert.Equal("12345678000199", body.RootElement.GetProperty("cpfCnpj").GetString());
        Assert.False(body.RootElement.GetProperty("notificationDisabled").GetBoolean());
    }

    [Fact]
    public async Task CreateSubscription_posts_cycle_billing_type_value_and_reference_when_none_is_active()
    {
        var h = new FakeHandler(
            Json(HttpStatusCode.OK, """{"data":[]}"""),
            Json(HttpStatusCode.OK,
                """{"id":"sub_1","customer":"cus_1","status":"ACTIVE","value":2990,"cycle":"YEARLY","billingType":"CREDIT_CARD","nextDueDate":"2026-12-20","externalReference":"tenant-1"}"""));
        var (sut, _) = Create(h);

        var info = await sut.CreateSubscriptionAsync(Subscription());

        Assert.Equal(2, h.Calls.Count);
        Assert.Equal(
            $"{BaseUrl}/subscriptions?customer=cus_1&externalReference=tenant-1&status=ACTIVE",
            h.Calls[0].Url);
        var create = h.Calls[1];
        Assert.Equal(HttpMethod.Post, create.Method);
        Assert.Equal($"{BaseUrl}/subscriptions", create.Url);
        Assert.Equal(RootKey, create.AccessToken);
        using var body = JsonDocument.Parse(create.Body!);
        Assert.Equal("cus_1", body.RootElement.GetProperty("customer").GetString());
        Assert.Equal("CREDIT_CARD", body.RootElement.GetProperty("billingType").GetString());
        Assert.Equal("YEARLY", body.RootElement.GetProperty("cycle").GetString());
        Assert.Equal(2990m, body.RootElement.GetProperty("value").GetDecimal());
        Assert.Equal("2026-12-20", body.RootElement.GetProperty("nextDueDate").GetString());
        Assert.Equal("tenant-1", body.RootElement.GetProperty("externalReference").GetString());
        Assert.Equal(
            new BillingSubscriptionInfo(
                "sub_1", "cus_1", BillingSubscriptionState.Active, false, 2990m, BillingCycleKind.Yearly,
                BillingMethod.CreditCard, new DateOnly(2026, 12, 20), "tenant-1"),
            info);
    }

    [Fact]
    public async Task CreateSubscription_returns_the_active_one_instead_of_creating_a_second_on_retry()
    {
        var h = new FakeHandler(Json(HttpStatusCode.OK,
            """{"data":[{"id":"sub_old","customer":"cus_1","status":"ACTIVE","value":189,"cycle":"MONTHLY","billingType":"PIX","externalReference":"tenant-1"}]}"""));
        var (sut, _) = Create(h);

        var info = await sut.CreateSubscriptionAsync(Subscription());

        Assert.Equal("sub_old", info.Id);
        var call = Assert.Single(h.Calls);
        Assert.Equal(HttpMethod.Get, call.Method);
    }

    [Fact]
    public async Task UpdateSubscription_puts_only_the_given_fields_and_the_pending_payments_flag()
    {
        var h = new FakeHandler(Json(HttpStatusCode.OK,
            """{"id":"sub_1","status":"ACTIVE","value":299,"cycle":"MONTHLY","billingType":"PIX"}"""));
        var (sut, _) = Create(h);

        await sut.UpdateSubscriptionAsync(
            "sub_1", new BillingSubscriptionUpdate(299m, BillingCycleKind.Monthly, UpdatePendingPayments: true));

        var call = Assert.Single(h.Calls);
        Assert.Equal(HttpMethod.Put, call.Method);
        Assert.Equal($"{BaseUrl}/subscriptions/sub_1", call.Url);
        using var body = JsonDocument.Parse(call.Body!);
        Assert.Equal(299m, body.RootElement.GetProperty("value").GetDecimal());
        Assert.Equal("MONTHLY", body.RootElement.GetProperty("cycle").GetString());
        Assert.True(body.RootElement.GetProperty("updatePendingPayments").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("billingType", out _));
    }

    [Fact]
    public async Task CancelSubscription_deletes_it_and_treats_a_missing_subscription_as_cancelled()
    {
        var h = new FakeHandler(
            Json(HttpStatusCode.OK, """{"deleted":true,"id":"sub_1"}"""),
            Json(HttpStatusCode.NotFound, """{"errors":[{"code":"invalid_object"}]}"""));
        var (sut, _) = Create(h);

        await sut.CancelSubscriptionAsync("sub_1");
        await sut.CancelSubscriptionAsync("sub_1");

        Assert.Equal(2, h.Calls.Count);
        Assert.All(h.Calls, call => Assert.Equal(HttpMethod.Delete, call.Method));
        Assert.All(h.Calls, call => Assert.Equal($"{BaseUrl}/subscriptions/sub_1", call.Url));
    }

    [Fact]
    public async Task CancelSubscription_surfaces_other_failures()
    {
        var h = new FakeHandler(Json(HttpStatusCode.InternalServerError, "{}"));
        var (sut, _) = Create(h);

        var ex = await Assert.ThrowsAsync<PaymentGatewayException>(() => sut.CancelSubscriptionAsync("sub_1"));

        Assert.Equal(500, ex.HttpStatus);
    }

    [Fact]
    public async Task GetSubscription_maps_state_and_deleted_flag()
    {
        var h = new FakeHandler(Json(HttpStatusCode.OK,
            """{"id":"sub_1","customer":"cus_1","status":"INACTIVE","deleted":true,"value":99.9,"cycle":"MONTHLY","billingType":"BOLETO"}"""));
        var (sut, _) = Create(h);

        var info = await sut.GetSubscriptionAsync("sub_1");

        Assert.Equal(BillingSubscriptionState.Inactive, info.State);
        Assert.True(info.Deleted);
        Assert.Equal(BillingMethod.Boleto, info.Method);
        Assert.Equal(99.9m, info.Value);
        Assert.Equal($"{BaseUrl}/subscriptions/sub_1", Assert.Single(h.Calls).Url);
    }

    [Theory]
    [InlineData("RECEIVED", GatewayChargeStatus.Paid)]
    [InlineData("CONFIRMED", GatewayChargeStatus.Paid)]
    [InlineData("PENDING", GatewayChargeStatus.Pending)]
    [InlineData("OVERDUE", GatewayChargeStatus.Overdue)]
    [InlineData("REFUNDED", GatewayChargeStatus.Refunded)]
    [InlineData("SOMETHING_NEW", GatewayChargeStatus.Unknown)]
    public async Task GetPayment_maps_status_dates_subscription_and_customer(string wire, GatewayChargeStatus expected)
    {
        var h = new FakeHandler(Json(HttpStatusCode.OK,
            $$"""{"id":"pay_1","status":"{{wire}}","value":189,"dueDate":"2026-12-10","paymentDate":"2026-12-11","invoiceUrl":"https://inv","subscription":"sub_1","customer":"cus_1","billingType":"PIX"}"""));
        var (sut, _) = Create(h);

        var payment = await sut.GetPaymentAsync("pay_1");

        Assert.Equal(expected, payment.Status);
        Assert.Equal(new DateOnly(2026, 12, 10), payment.DueDate);
        Assert.Equal(new DateOnly(2026, 12, 11), payment.PaymentDate);
        Assert.Equal("sub_1", payment.SubscriptionId);
        Assert.Equal("cus_1", payment.CustomerId);
        Assert.Equal(BillingMethod.Pix, payment.Method);
        Assert.Equal($"{BaseUrl}/payments/pay_1", Assert.Single(h.Calls).Url);
    }

    [Fact]
    public async Task ListSubscriptionPayments_and_the_pix_qr_code_use_the_root_key()
    {
        var h = new FakeHandler(
            Json(HttpStatusCode.OK,
                """{"data":[{"id":"pay_2","status":"PENDING","value":189,"dueDate":"2026-12-10","invoiceUrl":"https://inv/2"},{"id":"pay_1","status":"RECEIVED","value":189,"dueDate":"2026-11-10"}]}"""),
            Json(HttpStatusCode.OK, """{"encodedImage":"QUJD","payload":"000201pix"}"""));
        var (sut, _) = Create(h);

        var payments = await sut.ListSubscriptionPaymentsAsync("sub_1");
        var qr = await sut.GetPaymentPixQrCodeAsync("pay_2");

        Assert.Equal(["pay_2", "pay_1"], payments.Select(p => p.Id).ToArray());
        Assert.Equal(new BillingPixQrCode("QUJD", "000201pix"), qr);
        Assert.Equal($"{BaseUrl}/subscriptions/sub_1/payments", h.Calls[0].Url);
        Assert.Equal($"{BaseUrl}/payments/pay_2/pixQrCode", h.Calls[1].Url);
        Assert.All(h.Calls, call => Assert.Equal(RootKey, call.AccessToken));
    }

    [Fact]
    public async Task Without_a_root_key_every_call_fails_with_a_configuration_exception_before_any_request()
    {
        var h = new FakeHandler(Json(HttpStatusCode.OK, "{}"));
        var (sut, _) = Create(h, rootKey: null);

        await Assert.ThrowsAsync<PaymentConfigurationException>(() => sut.CreateOrGetCustomerAsync(Customer()));
        await Assert.ThrowsAsync<PaymentConfigurationException>(() => sut.CreateSubscriptionAsync(Subscription()));
        await Assert.ThrowsAsync<PaymentConfigurationException>(() => sut.GetPaymentAsync("pay_1"));
        await Assert.ThrowsAsync<PaymentConfigurationException>(() => sut.CancelSubscriptionAsync("sub_1"));
        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task A_failing_call_hides_the_body_and_the_root_key_from_the_exception_and_the_logs()
    {
        var leaky = $$"""{"errors":[{"code":"invalid_customer","description":"key {{RootKey}} CPF 52998224725 rejected"}]}""";
        var h = new FakeHandler(Json(HttpStatusCode.BadRequest, leaky));
        var (sut, logs) = Create(h);

        var ex = await Assert.ThrowsAsync<PaymentGatewayException>(() => sut.GetPaymentAsync("pay_1"));

        Assert.Equal("invalid_customer", ex.Code);
        Assert.Equal(400, ex.HttpStatus);
        Assert.DoesNotContain(RootKey, ex.ToString());
        Assert.DoesNotContain("52998224725", ex.ToString());
        Assert.DoesNotContain(logs.Messages, m => m.Contains(RootKey, StringComparison.Ordinal));
        Assert.NotEmpty(logs.Messages);
    }

    private static BillingCustomerRequest Customer() =>
        new("Quadra Ltda", "owner@club.test", "12345678000199", "tenant-1", "11999998888");

    private static BillingSubscriptionRequest Subscription() =>
        new("cus_1", BillingCycleKind.Yearly, BillingMethod.CreditCard, 2990m, new DateOnly(2026, 12, 20), "Rolvix Completo", "tenant-1");

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (AsaasGateway Sut, CapturingLogger<AsaasGateway> Logs) Create(FakeHandler handler, string? rootKey = RootKey)
    {
        var logs = new CapturingLogger<AsaasGateway>();
        var options = new AsaasOptions { BaseUrl = BaseUrl, RootApiKey = rootKey };
        return (new AsaasGateway(new HttpClient(handler), new StaticAsaasOptions(options), logs), logs);
    }

    private sealed record Call(HttpMethod Method, string Url, string? AccessToken, string? Body);

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _queue;

        public FakeHandler(params HttpResponseMessage[] responses) => _queue = new Queue<HttpResponseMessage>(responses);

        public List<Call> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = request.Headers.TryGetValues("access_token", out var values) ? values.Single() : null;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add(new Call(request.Method, request.RequestUri!.OriginalString, token, body));
            return _queue.Count > 0 ? _queue.Dequeue() : throw new InvalidOperationException("No response queued.");
        }
    }
}
