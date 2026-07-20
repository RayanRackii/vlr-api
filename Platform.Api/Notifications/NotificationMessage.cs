namespace Platform.Api.Notifications;

public sealed record NotificationMessage(
    string Type,
    string Recipient,
    string Subject,
    string Body);
