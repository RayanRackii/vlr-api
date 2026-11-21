namespace Platform.Api.Modules.Payments.Gateways.Asaas;

/// <summary>
/// Bound from <c>Payments:Asaas</c>. <see cref="RootApiKey"/> and <see cref="BillingWebhookToken"/> are secrets: the root key
/// creates subaccounts and bills tenants, the token authenticates the Rolvix billing webhook. Neither is ever logged.
/// </summary>
public sealed class AsaasOptions
{
    public const string SectionName = "Payments:Asaas";

    /// <summary>For example <c>https://api-sandbox.asaas.com/v3</c>.</summary>
    public string? BaseUrl { get; set; }

    public string? RootApiKey { get; set; }

    /// <summary>Shared secret Asaas sends in the <c>asaas-access-token</c> header of the Rolvix billing webhook.</summary>
    public string? BillingWebhookToken { get; set; }

    /// <summary>The platform wallet that receives the optional platform fee split.</summary>
    public string? PlatformWalletId { get; set; }

    /// <summary>Public API base URL, used to build the webhook URL.</summary>
    public string? WebhookBaseUrl { get; set; }
}
