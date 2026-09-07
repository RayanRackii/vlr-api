using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Notifications.Dtos;

public sealed record NotificationChannelConfigGroupResponse(
    string Module,
    IReadOnlyList<NotificationEventConfigResponse> Events);

public sealed record NotificationEventConfigResponse(
    string EventType,
    string DisplayKey,
    IReadOnlyList<NotificationChannelStateResponse> Channels);

public sealed record NotificationChannelStateResponse(
    NotificationChannel Channel,
    bool IsActive,
    bool Configurable);

public sealed record UpsertNotificationChannelConfigRequest
{
    public required string EventType { get; init; }

    public required NotificationChannel Channel { get; init; }

    public required bool IsActive { get; init; }
}

public sealed record UpsertNotificationChannelConfigResponse(
    string EventType,
    NotificationChannel Channel,
    bool IsActive);
