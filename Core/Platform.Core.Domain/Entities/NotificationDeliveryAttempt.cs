using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Entities;

public class NotificationDeliveryAttempt : Entity
{
    public required Guid DeliveryId { get; set; }

    public required int AttemptNumber { get; set; }

    public required DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public NotificationAttemptOutcome Outcome { get; set; }

    public string? ProviderResponse { get; set; }

    public string? ErrorMessage { get; set; }

    public NotificationDelivery Delivery { get; set; } = null!;

    public void Finish(NotificationAttemptOutcome outcome, string? providerResponse, string? errorMessage)
    {
        FinishedAt = DateTimeOffset.UtcNow;
        Outcome = outcome;
        ProviderResponse = Truncate(providerResponse, 2000);
        ErrorMessage = Truncate(errorMessage, 1000);
        Touch();
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value;
        }

        return value[..max];
    }
}
