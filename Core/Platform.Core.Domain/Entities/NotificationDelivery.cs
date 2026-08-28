using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Entities;

public class NotificationDelivery : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid NotificationId { get; set; }

    public required NotificationChannel Channel { get; set; }

    public required NotificationRecipientKind RecipientKind { get; set; }

    public Guid? RecipientId { get; set; }

    public string? RecipientName { get; set; }

    public string? RecipientEmail { get; set; }

    public string? RecipientPhone { get; set; }

    public NotificationDeliveryStatus Status { get; set; } = NotificationDeliveryStatus.Queued;

    public string? ProviderMessageId { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTimeOffset? NextAttemptAt { get; set; }

    public int AttemptCount { get; set; }

    public Notification Notification { get; set; } = null!;

    private readonly List<NotificationDeliveryAttempt> _attempts = [];

    public IReadOnlyCollection<NotificationDeliveryAttempt> Attempts => _attempts.AsReadOnly();

    public void AddAttempt(NotificationDeliveryAttempt attempt) => _attempts.Add(attempt);

    public void MarkDelivered()
    {
        Status = NotificationDeliveryStatus.Delivered;
        ErrorMessage = null;
        NextAttemptAt = null;
        Touch();
    }

    public void MarkQueuedForRetry(DateTimeOffset nextAttemptAt)
    {
        Status = NotificationDeliveryStatus.Queued;
        NextAttemptAt = nextAttemptAt;
        Touch();
    }

    public void MarkFailed(string error)
    {
        Status = NotificationDeliveryStatus.Failed;
        ErrorMessage = error;
        NextAttemptAt = null;
        Touch();
    }

    public void MarkSent(string? providerMessageId)
    {
        Status = NotificationDeliveryStatus.Sent;
        ProviderMessageId = providerMessageId;
        ErrorMessage = null;
        NextAttemptAt = null;
        Touch();
    }

    public void ResetForResend()
    {
        if (Status != NotificationDeliveryStatus.Failed)
        {
            throw new InvalidOperationException("Only failed deliveries can be resent.");
        }

        Status = NotificationDeliveryStatus.Queued;
        ErrorMessage = null;
        NextAttemptAt = DateTimeOffset.UtcNow;
        Touch();
    }
}
