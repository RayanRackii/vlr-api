using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Modules.CustomerAuth.PhoneVerification;

namespace Platform.Api.Tests.CustomerAuth;

public sealed class TwilioVerifyPhoneVerificationClientTests
{
    private const string ApiKeySid = "keysid";
    private const string ApiKeySecret = "keysecret";
    private const string VerifyServiceSid = "VAtestservice";
    private const string Phone = "+5511999991111";

    [Fact]
    public async Task Start_pending_succeeds_with_form_and_basic_auth()
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.Created, """{"status":"pending"}"""));
        var sut = CreateSut(handler);

        await sut.StartVerificationAsync(Phone, CancellationToken.None);

        AssertRequest(handler, $"/v2/Services/{VerifyServiceSid}/Verifications");
        Assert.Equal("application/x-www-form-urlencoded", handler.ContentType);
        Assert.Contains("Channel=sms", handler.Body, StringComparison.Ordinal);
        Assert.Contains("To=", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Code=", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_approved_succeeds()
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, """{"status":"APPROVED"}"""));
        var sut = CreateSut(handler);

        await sut.CheckVerificationAsync(Phone, "123456", CancellationToken.None);

        AssertRequest(handler, $"/v2/Services/{VerifyServiceSid}/VerificationCheck");
        Assert.Contains("Code=123456", handler.Body, StringComparison.Ordinal);
        Assert.Contains("To=", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_404_throws_invalid()
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.NotFound, """{"code":60200,"status":404}"""));
        var sut = CreateSut(handler);

        var ex = await Assert.ThrowsAsync<PhoneVerificationInvalidException>(
            () => sut.CheckVerificationAsync(Phone, "000000", CancellationToken.None));

        Assert.Equal(TwilioVerifyPhoneVerificationClient.InvalidOrExpiredMessage, ex.Message);
    }

    [Fact]
    public async Task Start_429_throws_rate_limited()
    {
        var handler = new RecordingHandler(Json((HttpStatusCode)429, """{"code":60203,"status":429}"""));
        var sut = CreateSut(handler);

        var ex = await Assert.ThrowsAsync<PhoneVerificationRateLimitedException>(
            () => sut.StartVerificationAsync(Phone, CancellationToken.None));

        Assert.Equal(TwilioVerifyPhoneVerificationClient.RateLimitedMessage, ex.Message);
    }

    [Fact]
    public async Task Start_500_throws_provider()
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.InternalServerError, """{"status":500}"""));
        var sut = CreateSut(handler);

        var ex = await Assert.ThrowsAsync<PhoneVerificationProviderException>(
            () => sut.StartVerificationAsync(Phone, CancellationToken.None));

        Assert.Equal(TwilioVerifyPhoneVerificationClient.ProviderUnavailableMessage, ex.Message);
    }

    [Fact]
    public async Task Missing_config_throws_provider_without_http()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("HTTP must not be called."));
        var sut = CreateSut(
            handler,
            new TwilioVerifyOptions
            {
                ApiKeySid = " ",
                ApiKeySecret = ApiKeySecret,
                VerifyServiceSid = VerifyServiceSid,
            });

        var ex = await Assert.ThrowsAsync<PhoneVerificationProviderException>(
            () => sut.StartVerificationAsync(Phone, CancellationToken.None));

        Assert.Equal(TwilioVerifyPhoneVerificationClient.ProviderUnavailableMessage, ex.Message);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task Check_canceled_throws_invalid_canceled_message()
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, """{"status":"canceled"}"""));
        var sut = CreateSut(handler);

        var ex = await Assert.ThrowsAsync<PhoneVerificationInvalidException>(
            () => sut.CheckVerificationAsync(Phone, "123456", CancellationToken.None));

        Assert.Equal(TwilioVerifyPhoneVerificationClient.CanceledMessage, ex.Message);
    }

    private static TwilioVerifyPhoneVerificationClient CreateSut(
        RecordingHandler handler,
        TwilioVerifyOptions? options = null)
    {
        var resolved = options ?? new TwilioVerifyOptions
        {
            AccountSid = "ACtest",
            ApiKeySid = ApiKeySid,
            ApiKeySecret = ApiKeySecret,
            VerifyServiceSid = VerifyServiceSid,
            BaseUrl = "https://verify.twilio.com/v2/",
        };

        return new TwilioVerifyPhoneVerificationClient(
            new HttpClient(handler),
            Options.Create(resolved),
            NullLogger<TwilioVerifyPhoneVerificationClient>.Instance);
    }

    private static void AssertRequest(RecordingHandler handler, string absolutePathSuffix)
    {
        Assert.NotNull(handler.Request);
        Assert.Equal(HttpMethod.Post, handler.Request.Method);
        Assert.Equal("Basic", handler.Request.Headers.Authorization?.Scheme);
        Assert.NotNull(handler.Request.Headers.Authorization?.Parameter);
        Assert.Equal(
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{ApiKeySid}:{ApiKeySecret}")),
            handler.Request.Headers.Authorization!.Parameter);
        Assert.EndsWith(absolutePathSuffix, handler.Request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public RecordingHandler(HttpResponseMessage response)
            : this(_ => response)
        {
        }

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public HttpRequestMessage? Request { get; private set; }

        public string Body { get; private set; } = string.Empty;

        public string? ContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            if (request.Content is not null)
            {
                Body = await request.Content.ReadAsStringAsync(cancellationToken);
                ContentType = request.Content.Headers.ContentType?.MediaType;
            }

            return _respond(request);
        }
    }
}
