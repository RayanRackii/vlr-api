namespace Platform.Api.Modules.Notifications.Dtos;

/// <summary>Unread in-app summary for the signed-in staff member: total plus per-event groups.</summary>
public sealed record MeNotificationSummaryResponse(
    int UnreadTotal,
    IReadOnlyList<MeNotificationGroup> Groups);

public sealed record MeNotificationGroup(
    string EventType,
    string Module,
    int Count,
    DateTimeOffset LatestAt,
    string? TargetRoute);

/// <summary>A page of the caller's in-app inbox items, newest first.</summary>
public sealed record MeNotificationsListResponse(
    IReadOnlyList<MeNotificationItem> Items,
    string? NextCursor);

public sealed record MeNotificationItem(
    Guid Id,
    string EventType,
    string Module,
    string AggregateType,
    Guid AggregateId,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt,
    string? TargetRoute);

/// <summary>
/// Exactly one of <see cref="Ids"/>, <see cref="EventType"/>, or <see cref="All"/> must be supplied.
/// </summary>
public sealed record MarkNotificationsReadRequest(
    IReadOnlyList<Guid>? Ids,
    string? EventType,
    bool? All);

public sealed record MarkNotificationsReadResponse(int Updated);
