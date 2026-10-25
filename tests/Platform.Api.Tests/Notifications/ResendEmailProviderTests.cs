using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Notifications;
using Platform.Api.Notifications.Providers.Resend;

namespace Platform.Api.Tests.Notifications;

public sealed class ResendEmailProviderTests
{
    [Fact]
    public async Task SendAsync_daily_quota_429_is_deferred()
    {
        using var client = new HttpClient(new ResponseHandler(
            HttpStatusCode.TooManyRequests,
            """{"statusCode":429,"name":"daily_quota_exceeded","message":"Daily quota exceeded"}"""));
        var provider = CreateProvider(client);

        var error = await Assert.ThrowsAsync<EmailDeferredException>(
            () => provider.SendAsync("recipient@example.test", "Subject", "Body"));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.DoesNotContain("recipient", error.Message);
    }

    [Theory]
    [InlineData(429, """{"name":"rate_limit_exceeded"}""")]
    [InlineData(429, "not json")]
    [InlineData(429, "null")]
    [InlineData(429, "[]")]
    [InlineData(429, "{}")]
    [InlineData(429, """{"name":429}""")]
    [InlineData(429, """{"name":null}""")]
    [InlineData(400, """{"name":"daily_quota_exceeded"}""")]
    [InlineData(500, """{"name":"daily_quota_exceeded"}""")]
    public async Task SendAsync_other_errors_keep_existing_failure_behavior(int status, string body)
    {
        using var client = new HttpClient(new ResponseHandler((HttpStatusCode)status, body));
        var provider = CreateProvider(client);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.SendAsync("recipient@example.test", "Subject", "Body"));
    }

    [Fact]
    public async Task SendAsync_success_is_unchanged()
    {
        using var client = new HttpClient(new ResponseHandler(HttpStatusCode.OK, """{"id":"test-id"}"""));

        await CreateProvider(client).SendAsync("recipient@example.test", "Subject", "Body");
    }

    internal static ResendEmailProvider CreateProvider(HttpClient client) => new(
        client,
        Options.Create(new ResendOptions { ApiKey = "test-key", FromEmail = "sender@example.test" }),
        NullLogger<ResendEmailProvider>.Instance);

    internal sealed class ResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
