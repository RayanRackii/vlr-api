namespace Platform.Api.Notifications;

public sealed class NotificationsOptions
{
    public const string SectionName = "Notifications";

    /// <summary>
    /// Tri-state gate for Resend/Meta. <see langword="null"/> (unset) resolves
    /// to false in Development and true in every other environment.
    /// </summary>
    public bool? AllowExternalDelivery { get; set; }
}
