using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Platform.Api.Modules.Payments.Gateways.Asaas;

/// <summary>
/// Compares the <c>asaas-access-token</c> header with <c>Payments:Asaas:BillingWebhookToken</c>. Both sides are
/// hashed first so the comparison is constant time whatever the length of the presented value. The configured
/// token is never logged.
/// </summary>
public sealed class AsaasBillingWebhookTokenValidator(IOptionsMonitor<AsaasOptions> options) : IBillingWebhookTokenValidator
{
    public BillingWebhookTokenCheck Validate(string? presentedToken)
    {
        var configured = options.CurrentValue.BillingWebhookToken;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return BillingWebhookTokenCheck.NotConfigured;
        }

        if (string.IsNullOrEmpty(presentedToken))
        {
            return BillingWebhookTokenCheck.Invalid;
        }

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(configured)),
            SHA256.HashData(Encoding.UTF8.GetBytes(presentedToken)))
            ? BillingWebhookTokenCheck.Valid
            : BillingWebhookTokenCheck.Invalid;
    }
}
