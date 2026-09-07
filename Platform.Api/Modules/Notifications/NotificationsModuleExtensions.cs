using Platform.Api.Modules.Notifications.Services;

namespace Platform.Api.Modules.Notifications;

public static class NotificationsModuleExtensions
{
    public static IServiceCollection AddTenantNotificationsModule(this IServiceCollection services)
    {
        services.AddScoped<INotificationChannelConfigService, NotificationChannelConfigService>();
        return services;
    }
}
