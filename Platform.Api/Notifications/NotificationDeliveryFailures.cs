using Platform.Core.Domain.Enums;

namespace Platform.Api.Notifications;

internal static class NotificationDeliveryFailures
{
    public const string SmsUnavailable = "SMS channel is not available.";

    public const string NoExternalProvider = "Channel has no external provider.";

    public const string EmailMissing = "Email recipient is missing.";

    public const string WhatsAppMissing = "WhatsApp recipient is missing.";

    public static string? MissingContact(NotificationChannel channel, string? email, string? phone) =>
        channel switch
        {
            NotificationChannel.Email when string.IsNullOrWhiteSpace(email) => EmailMissing,
            NotificationChannel.WhatsApp when string.IsNullOrWhiteSpace(phone) => WhatsAppMissing,
            _ => null,
        };

    public static string Unrecoverable(NotificationChannel channel) => channel switch
    {
        NotificationChannel.Sms => SmsUnavailable,
        NotificationChannel.InApp or NotificationChannel.Push => NoExternalProvider,
        _ => $"Unsupported channel {channel}.",
    };

    public static bool CanResend(
        NotificationDeliveryStatus status,
        NotificationChannel channel,
        string? errorMessage)
    {
        if (status != NotificationDeliveryStatus.Failed)
        {
            return false;
        }

        if (channel is NotificationChannel.Sms or NotificationChannel.Push or NotificationChannel.InApp)
        {
            return false;
        }

        if (errorMessage is SmsUnavailable or NoExternalProvider)
        {
            return false;
        }

        return errorMessage is null
            || !errorMessage.StartsWith("Unsupported channel ", StringComparison.Ordinal);
    }
}
