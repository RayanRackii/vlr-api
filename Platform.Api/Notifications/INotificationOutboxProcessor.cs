namespace Platform.Api.Notifications;

public interface INotificationOutboxProcessor
{
    Task ProcessDueAsync(CancellationToken cancellationToken = default);

    Task ProcessDeliveryAsync(Guid deliveryId, CancellationToken cancellationToken = default);

    Task ResendAsync(Guid deliveryId, CancellationToken cancellationToken = default);
}
