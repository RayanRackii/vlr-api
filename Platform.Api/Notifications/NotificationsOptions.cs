namespace Platform.Api.Notifications;

public sealed class NotificationsOptions
{
    public const string SectionName = "Notifications";

    /// <summary>
    /// Legacy tri-state gate for Resend and Meta together. Used only when a
    /// channel-specific flag is unset. Unset/null is false in every environment.
    /// </summary>
    public bool? AllowExternalDelivery { get; set; }

    /// <summary>
    /// Channel-specific gate for Resend. When unset, falls back to
    /// <see cref="AllowExternalDelivery"/>.
    /// </summary>
    public bool? AllowExternalEmail { get; set; }

    /// <summary>
    /// Channel-specific gate for Meta WhatsApp. When unset, falls back to
    /// <see cref="AllowExternalDelivery"/>.
    /// </summary>
    public bool? AllowExternalWhatsApp { get; set; }
}
