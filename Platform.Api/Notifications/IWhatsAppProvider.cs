namespace Platform.Api.Notifications;

public interface IWhatsAppProvider
{
    /// <summary>
    /// Mensagem de texto livre. Só é entregue dentro da janela de atendimento
    /// de 24h aberta pelo cliente; fora dela o Meta rejeita o envio.
    /// Company-initiated catalog/rentals notifications must not use this path.
    /// </summary>
    Task SendAsync(
        string recipient,
        string body,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Mensagem de template aprovado no Meta. Returns the Graph message id
    /// (<c>messages[0].id</c>) when the provider reports one.
    /// </summary>
    Task<string?> SendTemplateAsync(
        string recipient,
        string templateName,
        string languageCode,
        IReadOnlyList<string> bodyParameters,
        CancellationToken cancellationToken = default);
}
