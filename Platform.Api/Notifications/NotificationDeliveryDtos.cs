using Platform.Core.Domain.Enums;

namespace Platform.Api.Notifications;

public sealed record NotificationDeliveryFilter(
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? EventType,
    NotificationRecipientKind? RecipientKind,
    NotificationChannel? Channel,
    NotificationDeliveryStatus? Status);

public static class NotificationDeliveryPages
{
    public const int DefaultPageSize = 20;

    public const int MaxPageSize = 50;

    public static bool TryCreate(int page, int pageSize, out string error)
    {
        if (page < 1)
        {
            error = "Page must be at least 1.";
            return false;
        }

        if (pageSize < 1 || pageSize > MaxPageSize)
        {
            error = $"Page size must be between 1 and {MaxPageSize}.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

public sealed record NotificationDeliveryAttemptResponse(
    int AttemptNumber,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    NotificationAttemptOutcome Outcome,
    string? ErrorMessage,
    NotificationChannel Channel);

public sealed record NotificationDeliveryResponse(
    Guid Id,
    Guid NotificationId,
    string EventKey,
    string? Module,
    NotificationChannel Channel,
    NotificationRecipientKind RecipientKind,
    Guid? RecipientId,
    string? RecipientName,
    NotificationDeliveryStatus Status,
    int AttemptCount,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    bool CanResend,
    IReadOnlyList<NotificationDeliveryAttemptResponse> Attempts);

public sealed record NotificationDeliveryPageResponse(
    IReadOnlyList<NotificationDeliveryResponse> Items,
    int Page,
    int PageSize,
    int TotalCount);
