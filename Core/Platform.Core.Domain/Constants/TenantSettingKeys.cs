namespace Platform.Core.Domain.Constants;

public static class TenantSettingKeys
{
    /// <summary>IANA zone id for the tenant booking clock, e.g. "America/Manaus".</summary>
    public const string BookingTimeZoneId = "booking.timeZoneId";

    /// <summary>
    /// JSON array of rental asset ids (strings) on which the tenant allows teacher lessons.
    /// A missing, empty or malformed value means lessons are disabled for the tenant.
    /// </summary>
    public const string TeacherLessonRentalAssetIds = "rentals.teacher_lessons.rental_asset_ids";

    /// <summary>"true" or "false": whether the tenant uses the booking map UI. Missing means false.</summary>
    public const string BookingUiEnabled = "rentals.booking_ui.enabled";

    /// <summary>
    /// The only keys a platform admin may write through the admin settings endpoint.
    /// A key must be registered here and have a validator before it can be edited remotely.
    /// </summary>
    public static readonly IReadOnlyList<string> AdminEditableKeys =
    [
        TeacherLessonRentalAssetIds,
        BookingUiEnabled,
    ];
}
