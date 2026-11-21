using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Services.Billing;

namespace Platform.Api.Modules.Billing.Controllers;

/// <summary>
/// Asaas webhook of Rolvix's own billing (root account). Anonymous: it is authenticated by the shared token in
/// the <c>asaas-access-token</c> header. It answers 200 as soon as the event is queued; the subscription changes
/// in a background job. A failure to queue never leaks details to the caller.
/// </summary>
[ApiController]
[Route("api/webhooks/payments/asaas/billing")]
public sealed class AsaasBillingWebhookController(
    IBillingWebhookIntake intake,
    ILogger<AsaasBillingWebhookController> logger) : ControllerBase
{
    /// <summary>Asaas payloads are a few KB; anything bigger is not from Asaas.</summary>
    public const int MaxPayloadChars = 256 * 1024;

    [HttpPost]
    [AllowAnonymous]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        var token = Request.Headers[BillingWebhookEvents.TokenHeader].ToString();

        var payload = await ReadBodyAsync(cancellationToken);
        if (payload is null)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "Payload too large." });
        }

        BillingWebhookOutcome outcome;
        try
        {
            outcome = await intake.AcceptAsync(token, payload, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Asaas retries and eventually pauses a webhook that answers errors: a 5xx is right only when the
            // event could not be stored for processing, which is exactly this case.
            logger.LogError(ex, "Could not queue the billing webhook event.");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Event could not be queued." });
        }

        return outcome == BillingWebhookOutcome.Unauthorized
            ? Unauthorized(new { error = "Invalid token." })
            : Ok();
    }

    private async Task<string?> ReadBodyAsync(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var builder = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            builder.Append(buffer, 0, read);
            if (builder.Length > MaxPayloadChars)
            {
                return null;
            }
        }

        return builder.ToString();
    }
}
