namespace Platform.Api.Modules.Payments.Gateways.Asaas;

/// <summary>Bound from <c>Payments:Asaas</c>. <see cref="RootApiKey"/> is a secret and is used only to create subaccounts.</summary>
public sealed class AsaasOptions
{
    public const string SectionName = "Payments:Asaas";

    /// <summary>For example <c>https://api-sandbox.asaas.com/v3</c>.</summary>
    public string? BaseUrl { get; set; }

    public string? RootApiKey { get; set; }

    /// <summary>The platform wallet that receives the optional platform fee split.</summary>
    public string? PlatformWalletId { get; set; }

    /// <summary>Public API base URL, used to build the webhook URL.</summary>
    public string? WebhookBaseUrl { get; set; }
}
