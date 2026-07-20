namespace Platform.Api.Notifications;

public interface IWhatsAppProvider
{
    Task SendAsync(
        string recipient,
        string body,
        CancellationToken cancellationToken = default);
}
