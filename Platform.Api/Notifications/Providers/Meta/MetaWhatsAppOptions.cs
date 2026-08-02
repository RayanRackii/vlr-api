namespace Platform.Api.Notifications.Providers.Meta;

public sealed class MetaWhatsAppOptions
{
    public const string SectionName = "WhatsApp";

    public string GraphApiUrl { get; set; } = "https://graph.facebook.com/v25.0/";
    public string PhoneNumberId { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>Token combinado com o campo "Verificar token" do webhook no Meta for Developers.</summary>
    public string VerifyToken { get; set; } = string.Empty;

    /// <summary>App Secret do app Meta, usado para validar a assinatura X-Hub-Signature-256 dos webhooks.</summary>
    public string AppSecret { get; set; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(PhoneNumberId)
        && !string.IsNullOrWhiteSpace(AccessToken);
}
