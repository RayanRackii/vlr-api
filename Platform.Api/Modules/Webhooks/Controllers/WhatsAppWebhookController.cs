using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Platform.Api.Modules.Webhooks.Services;
using Platform.Api.Notifications.Providers.Meta;

namespace Platform.Api.Modules.Webhooks.Controllers;

[ApiController]
[Route("api/webhooks/whatsapp")]
public sealed class WhatsAppWebhookController(
    IOptions<MetaWhatsAppOptions> options,
    IWhatsAppWebhookProcessor processor,
    ILogger<WhatsAppWebhookController> logger) : ControllerBase
{
    private const string SignatureHeader = "X-Hub-Signature-256";

    /// <summary>
    /// Handshake de verificação do Meta: ecoa hub.challenge quando o
    /// hub.verify_token bate com o configurado em WhatsApp:VerifyToken.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    public IActionResult Verify(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        var configuredToken = options.Value.VerifyToken;

        var isValid = string.Equals(mode, "subscribe", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(configuredToken)
            && string.Equals(verifyToken, configuredToken, StringComparison.Ordinal);

        if (!isValid)
        {
            logger.LogWarning(
                "WhatsApp webhook verification rejected. Mode: {Mode}, token match: {TokenMatch}.",
                mode,
                string.Equals(verifyToken, configuredToken, StringComparison.Ordinal));

            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Verification failed." });
        }

        logger.LogInformation("WhatsApp webhook verified successfully.");

        return Content(challenge ?? string.Empty, "text/plain");
    }

    /// <summary>
    /// Ingestão de eventos (status de entrega, mensagens recebidas). O Meta
    /// exige resposta 200 em menos de 3 segundos; o processamento é leve e
    /// falhas de parse nunca derrubam a resposta.
    /// </summary>
    [HttpPost]
    [AllowAnonymous]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var payload = await reader.ReadToEndAsync(cancellationToken);

        if (!HasValidSignature(payload))
        {
            logger.LogWarning("WhatsApp webhook rejected: invalid {Header}.", SignatureHeader);
            return Unauthorized(new { error = "Invalid payload signature." });
        }

        try
        {
            processor.Process(payload);
        }
        catch (Exception ex)
        {
            // Nunca devolver erro ao Meta por falha interna de processamento,
            // senão o webhook entra em retry/backoff e pode ser desativado.
            logger.LogError(ex, "Failed to process WhatsApp webhook payload.");
        }

        return Ok();
    }

    private bool HasValidSignature(string payload)
    {
        var appSecret = options.Value.AppSecret;

        if (string.IsNullOrWhiteSpace(appSecret))
        {
            logger.LogWarning(
                "WhatsApp:AppSecret is not configured; skipping webhook signature validation.");
            return true;
        }

        var signatureHeader = Request.Headers[SignatureHeader].ToString();

        if (!signatureHeader.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var expected = signatureHeader["sha256=".Length..];

        var computed = Convert.ToHexStringLower(
            HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(appSecret),
                Encoding.UTF8.GetBytes(payload)));

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(expected.ToLowerInvariant()));
    }
}
