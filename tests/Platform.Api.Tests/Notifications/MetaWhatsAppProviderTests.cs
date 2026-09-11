using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Notifications;
using Platform.Api.Notifications.Providers.Meta;

namespace Platform.Api.Tests.Notifications;

public sealed class MetaWhatsAppProviderTests
{
    [Theory]
    [InlineData("catalog_order_status_update", "Club", "Ana", "#1", "Solicitado")]
    [InlineData("rental_reservation_status_update", "Clube", "Ana", "Quadra 1", "Confirmada")]
    [InlineData("rental_reservation_reminder", "Clube", "Ana", "Quadra 1", "01/09/2026 10:00")]
    public async Task SendTemplateAsync_posts_approved_body_template(
        string templateName,
        string p1,
        string p2,
        string p3,
        string p4)
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, """{"messages":[{"id":"wamid.abc"}]}"""));
        var sut = CreateSut(handler);

        var messageId = await sut.SendTemplateAsync(
            "+55 (11) 99999-0000",
            templateName,
            "pt_BR",
            [p1, p2, p3, p4],
            CancellationToken.None);

        Assert.Equal("wamid.abc", messageId);
        Assert.Equal("123456789", handler.Request!.RequestUri!.AbsolutePath.Trim('/').Split('/')[^2]);
        using var doc = JsonDocument.Parse(handler.Body);
        var root = doc.RootElement;
        Assert.Equal("whatsapp", root.GetProperty("messaging_product").GetString());
        Assert.Equal("5511999990000", root.GetProperty("to").GetString());
        Assert.Equal("template", root.GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("text", out _));
        var template = root.GetProperty("template");
        Assert.Equal(templateName, template.GetProperty("name").GetString());
        Assert.Equal("pt_BR", template.GetProperty("language").GetProperty("code").GetString());
        var parameters = template.GetProperty("components")[0].GetProperty("parameters");
        Assert.Equal(4, parameters.GetArrayLength());
        Assert.Equal(p1, parameters[0].GetProperty("text").GetString());
        Assert.Equal(p2, parameters[1].GetProperty("text").GetString());
        Assert.Equal(p3, parameters[2].GetProperty("text").GetString());
        Assert.Equal(p4, parameters[3].GetProperty("text").GetString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization?.Scheme);
    }

    [Fact]
    public async Task SendTemplateAsync_permanent_template_error_does_not_look_transient()
    {
        var handler = new RecordingHandler(
            Json(HttpStatusCode.BadRequest, """{"error":{"code":132001,"type":"OAuthException","message":"template not found"}}"""));
        var sut = CreateSut(handler);

        var ex = await Assert.ThrowsAsync<WhatsAppSendException>(
            () => sut.SendTemplateAsync("+5511999990000", "missing_template", "pt_BR", ["a", "b", "c", "d"], CancellationToken.None));

        Assert.False(ex.IsTransient);
        Assert.Equal(132001, ex.ProviderCode);
        Assert.DoesNotContain("5511999990000", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendTemplateAsync_429_is_transient()
    {
        var handler = new RecordingHandler(Json((HttpStatusCode)429, """{"error":{"code":4,"type":"OAuthException"}}"""));
        var sut = CreateSut(handler);

        var ex = await Assert.ThrowsAsync<WhatsAppSendException>(
            () => sut.SendTemplateAsync("+5511999990000", "catalog_order_status_update", "pt_BR", ["a", "b", "c", "d"], CancellationToken.None));

        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task SendTemplateAsync_500_is_transient()
    {
        var handler = new RecordingHandler(
            Json(HttpStatusCode.InternalServerError, """{"error":{"code":2,"type":"OAuthException"}}"""));
        var sut = CreateSut(handler);

        var ex = await Assert.ThrowsAsync<WhatsAppSendException>(
            () => sut.SendTemplateAsync("+5511999990000", "catalog_order_status_update", "pt_BR", ["a", "b", "c", "d"], CancellationToken.None));

        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task SendTemplateAsync_timeout_is_canceled_without_claiming_success()
    {
        var handler = new DelayedHandler(TimeSpan.FromSeconds(2));
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://graph.facebook.com/v25.0/"),
            Timeout = TimeSpan.FromMilliseconds(50),
        };
        var sut = new MetaWhatsAppProvider(
            client,
            Options.Create(new MetaWhatsAppOptions
            {
                GraphApiUrl = "https://graph.facebook.com/v25.0/",
                PhoneNumberId = "123456789",
                AccessToken = "test-token",
            }),
            NullLogger<MetaWhatsAppProvider>.Instance);

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            sut.SendTemplateAsync("+5511999990000", "catalog_order_status_update", "pt_BR", ["a", "b", "c", "d"], CancellationToken.None));
    }

    private static MetaWhatsAppProvider CreateSut(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://graph.facebook.com/v25.0/") };
        return new MetaWhatsAppProvider(
            client,
            Options.Create(new MetaWhatsAppOptions
            {
                GraphApiUrl = "https://graph.facebook.com/v25.0/",
                PhoneNumberId = "123456789",
                AccessToken = "test-token",
            }),
            NullLogger<MetaWhatsAppProvider>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            if (request.Content is not null)
            {
                Body = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return response;
        }
    }

    private sealed class DelayedHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"messages":[{"id":"wamid.late"}]}""", Encoding.UTF8, "application/json"),
            };
        }
    }
}
