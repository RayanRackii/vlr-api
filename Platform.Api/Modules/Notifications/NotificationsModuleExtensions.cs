using Platform.Api.Modules.Notifications.Services;
using Platform.Api.Notifications;

namespace Platform.Api.Modules.Notifications;

public static class NotificationsModuleExtensions
{
    public static IServiceCollection AddTenantNotificationsModule(this IServiceCollection services)
    {
        services.AddScoped<INotificationDeliveryService, NotificationDeliveryService>();
        services.AddScoped<INotificationChannelConfigService, NotificationChannelConfigService>();
        services.AddScoped<IMeNotificationsService, MeNotificationsService>();
        services.AddScoped<ICustomerNotificationsService, CustomerNotificationsService>();
        return services;
    }
}
