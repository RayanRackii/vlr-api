namespace Platform.Api.Notifications;

public interface INotificationOutboxScheduler
{
    void Schedule(Guid deliveryId);
}

public sealed class HangfireNotificationOutboxScheduler : INotificationOutboxScheduler
{
    public void Schedule(Guid deliveryId)
    {
        Hangfire.BackgroundJob.Enqueue<INotificationOutboxProcessor>(
            processor => processor.ProcessDeliveryAsync(deliveryId, CancellationToken.None));
    }
}

public sealed class NoOpNotificationOutboxScheduler : INotificationOutboxScheduler
{
    public void Schedule(Guid deliveryId)
    {
    }
}
