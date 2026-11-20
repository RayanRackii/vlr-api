using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Api.Http;
using Platform.Api.Modules.CustomerAuth.Controllers;

namespace Platform.Api.Tests.Http;

public sealed class CustomerAuthSendRateLimitTests
{
    [Fact]
    public async Task Eleventh_request_from_one_ip_gets_429_with_retry_after_and_error_body()
    {
        using var host = StartHost();
        using var client = host.GetTestClient();

        for (var i = 0; i < 10; i++)
        {
            using var ok = await Send(client, "/send", "203.0.113.5");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var limited = await Send(client, "/send", "203.0.113.5");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.TryGetValues("Retry-After", out var retryAfter));
        Assert.True(int.Parse(retryAfter!.Single(), System.Globalization.CultureInfo.InvariantCulture) >= 1);
        var body = await limited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(CustomerAuthSendRateLimiting.RejectionMessage, body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Another_ip_is_not_affected_and_unmarked_routes_are_never_limited()
    {
        using var host = StartHost();
        using var client = host.GetTestClient();
        for (var i = 0; i < 11; i++)
        {
            using var _ = await Send(client, "/send", "203.0.113.5");
        }

        using var other = await Send(client, "/send", "203.0.113.6");
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);

        for (var i = 0; i < 30; i++)
        {
            using var open = await Send(client, "/open", "203.0.113.5");
            Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        }
    }

    [Fact]
    public async Task Global_cap_applies_across_ips_and_limits_are_configurable()
    {
        using var host = StartHost(options =>
        {
            options.CustomerAuthSend.PermitLimitPerIp = 5;
            options.CustomerAuthSend.GlobalPermitLimit = 3;
        });
        using var client = host.GetTestClient();

        for (var i = 1; i <= 3; i++)
        {
            using var ok = await Send(client, "/send", $"203.0.113.{i}");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var limited = await Send(client, "/send", "203.0.113.77");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Theory]
    [InlineData(nameof(CustomerAuthController.Register))]
    [InlineData(nameof(CustomerAuthController.RequestOtp))]
    [InlineData(nameof(CustomerAuthController.ResendVerification))]
    [InlineData(nameof(CustomerAuthController.RequestPasswordReset))]
    public void Anonymous_send_endpoints_carry_the_throttle_marker(string action)
    {
        var method = typeof(CustomerAuthController).GetMethod(action);

        Assert.NotNull(method);
        Assert.NotNull(method!.GetCustomAttribute<CustomerAuthSendThrottleAttribute>());
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string path, string ip)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-Real-IP", ip);
        return client.SendAsync(request);
    }

    private static IHost StartHost(Action<RateLimitOptions>? configure = null) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddCustomerAuthSendRateLimiting();
                    if (configure is not null)
                    {
                        services.Configure(configure);
                    }
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/send", () => Results.Ok())
                            .WithMetadata(new CustomerAuthSendThrottleAttribute());
                        endpoints.MapPost("/open", () => Results.Ok());
                    });
                });
            })
            .Start();
}
