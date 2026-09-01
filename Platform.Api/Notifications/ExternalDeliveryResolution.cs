namespace Platform.Api.Notifications;

/// <summary>
/// Resolves whether an external notification channel may leave the process.
/// Unset never enables delivery. An explicit per-channel value wins over the
/// legacy global <see cref="NotificationsOptions.AllowExternalDelivery"/> flag.
/// </summary>
public static class ExternalDeliveryResolution
{
    public static bool IsEnabled(bool? channelSpecific, bool? globalFallback) =>
        (channelSpecific ?? globalFallback) == true;
}
