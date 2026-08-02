namespace Platform.Api.Notifications;

public interface IWhatsAppProvider
{
    /// <summary>
    /// Mensagem de texto livre. Só é entregue dentro da janela de atendimento
    /// de 24h aberta pelo cliente; fora dela o Meta rejeita o envio.
    /// </summary>
    Task SendAsync(
        string recipient,
        string body,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Mensagem de template aprovado no Meta (obrigatória para mensagens
    /// iniciadas pela empresa, ex.: OTP, confirmação de reserva).
    /// </summary>
    Task SendTemplateAsync(
        string recipient,
        string templateName,
        string languageCode,
        IReadOnlyList<string> bodyParameters,
        CancellationToken cancellationToken = default);
}
