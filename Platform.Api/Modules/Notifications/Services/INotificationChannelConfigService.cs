using Platform.Api.Modules.Notifications.Dtos;

namespace Platform.Api.Modules.Notifications.Services;

public interface INotificationChannelConfigService
{
    Task<IReadOnlyList<NotificationChannelConfigGroupResponse>> ListAsync(
        CancellationToken cancellationToken);

    Task<UpsertNotificationChannelConfigResponse> UpsertAsync(
        UpsertNotificationChannelConfigRequest request,
        CancellationToken cancellationToken);
}
