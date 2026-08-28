namespace Platform.Api.Notifications;

public sealed class NotificationsOptions
{
    public const string SectionName = "Notifications";

    /// <summary>
    /// Tri-state gate for Resend/Meta. External providers are registered only when
    /// this is explicitly <see langword="true"/> (unset/null is false in every environment).
    /// </summary>
    public bool? AllowExternalDelivery { get; set; }
}
