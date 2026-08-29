namespace Platform.Core.Domain.Enums;

public enum NotificationAttemptOutcome
{
    Success = 0,
    TransientFailure = 1,
    PermanentFailure = 2,
}
