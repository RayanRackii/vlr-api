namespace Platform.Api.Notifications;

public static class NotificationsServiceCollectionExtensions
{
    public static IServiceCollection AddNotificationInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<NotificationQueue>();
        services.AddScoped<IEmailProvider, DevEmailProvider>();
        services.AddScoped<IWhatsAppProvider, DevWhatsAppProvider>();
        services.AddHostedService<NotificationDispatcherService>();
        return services;
    }
}
