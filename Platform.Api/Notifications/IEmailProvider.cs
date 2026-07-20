namespace Platform.Api.Notifications;

public interface IEmailProvider
{
    Task SendAsync(
        string recipient,
        string subject,
        string body,
        CancellationToken cancellationToken = default);
}
