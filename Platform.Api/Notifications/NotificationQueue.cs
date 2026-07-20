using System.Threading.Channels;

namespace Platform.Api.Notifications;

public sealed class NotificationQueue
{
    private readonly Channel<NotificationMessage> _channel =
        Channel.CreateUnbounded<NotificationMessage>();

    public ValueTask EnqueueAsync(
        NotificationMessage message,
        CancellationToken cancellationToken = default)
    {
        return _channel.Writer.WriteAsync(message, cancellationToken);
    }

    public ValueTask<NotificationMessage> DequeueAsync(
        CancellationToken cancellationToken = default)
    {
        return _channel.Reader.ReadAsync(cancellationToken);
    }
}
