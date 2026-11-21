using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Modules.Webhooks.Controllers;
using Platform.Api.Modules.Webhooks.Services;
using Platform.Api.Notifications.Providers.Meta;
using Platform.Api.Tests.Fakes;

namespace Platform.Api.Tests.Webhooks;

public sealed class WhatsAppWebhookSignatureTests
{
    private const string Payload = "{\"entry\":[]}";

    private sealed class CountingProcessor : IWhatsAppWebhookProcessor
    {
        public int Calls { get; private set; }

        public void Process(string payload) => Calls++;
    }

    private static WhatsAppWebhookController Create(
        string environmentName,
        string appSecret,
        CountingProcessor processor,
        string? signature = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(Payload));
        if (signature is not null)
            http.Request.Headers["X-Hub-Signature-256"] = signature;

        return new WhatsAppWebhookController(
            Options.Create(new MetaWhatsAppOptions { AppSecret = appSecret }),
            processor,
            new FakeHostEnvironment { EnvironmentName = environmentName },
            NullLogger<WhatsAppWebhookController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Missing_secret_outside_development_is_rejected(string environmentName)
    {
        var processor = new CountingProcessor();
        var controller = Create(environmentName, string.Empty, processor);

        var result = await controller.Receive(CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(0, processor.Calls);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public async Task Missing_secret_in_development_or_testing_is_accepted(string environmentName)
    {
        var processor = new CountingProcessor();
        var controller = Create(environmentName, string.Empty, processor);

        var result = await controller.Receive(CancellationToken.None);

        Assert.IsType<OkResult>(result);
        Assert.Equal(1, processor.Calls);
    }

    [Fact]
    public async Task Configured_secret_still_requires_a_valid_signature()
    {
        var processor = new CountingProcessor();
        const string secret = "test-secret";
        var signature = "sha256=" + Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(Payload)));

        var good = await Create("Production", secret, processor, signature).Receive(CancellationToken.None);
        var bad = await Create("Production", secret, processor, "sha256=deadbeef").Receive(CancellationToken.None);

        Assert.IsType<OkResult>(good);
        Assert.IsType<UnauthorizedObjectResult>(bad);
        Assert.Equal(1, processor.Calls);
    }
}
