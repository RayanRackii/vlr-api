namespace Platform.Api.Notifications;

/// <summary>
/// Mensagem enfileirada no NotificationQueue. Para WhatsApp iniciado pela
/// empresa, preencha TemplateName/TemplateLanguage/TemplateParameters —
/// texto livre (Body) só é entregue dentro da janela de 24h do Meta.
/// </summary>
public sealed record NotificationMessage(
    string Type,
    string Recipient,
    string Subject,
    string Body,
    string? TemplateName = null,
    string? TemplateLanguage = null,
    IReadOnlyList<string>? TemplateParameters = null);
