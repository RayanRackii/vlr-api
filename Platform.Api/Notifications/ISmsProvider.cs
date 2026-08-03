namespace Platform.Api.Notifications;

public interface ISmsProvider
{
    Task SendAsync(
        string recipient,
        string body,
        CancellationToken cancellationToken = default);
}
