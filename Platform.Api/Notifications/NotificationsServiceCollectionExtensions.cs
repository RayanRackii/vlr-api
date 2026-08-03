using Platform.Api.Notifications.Providers.Dev;
using Platform.Api.Notifications.Providers.Meta;
using Platform.Api.Notifications.Providers.Resend;

namespace Platform.Api.Notifications;

public static class NotificationsServiceCollectionExtensions
{
    /// <summary>
    /// Registra a fila e o dispatcher de notificações. Os providers reais
    /// (Resend / Meta WhatsApp) só entram quando as credenciais estão
    /// configuradas; caso contrário caem nos providers Dev (log no console).
    /// </summary>
    public static IServiceCollection AddNotificationInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<NotificationQueue>();
        services.AddHostedService<NotificationDispatcherService>();

        services.Configure<ResendOptions>(configuration.GetSection(ResendOptions.SectionName));
        services.Configure<MetaWhatsAppOptions>(configuration.GetSection(MetaWhatsAppOptions.SectionName));

        var resendConfigured = !string.IsNullOrWhiteSpace(configuration["Resend:ApiKey"])
            && !string.IsNullOrWhiteSpace(configuration["Resend:FromEmail"]);

        if (resendConfigured)
        {
            services.AddHttpClient<IEmailProvider, ResendEmailProvider>();
        }
        else
        {
            services.AddScoped<IEmailProvider, DevEmailProvider>();
        }

        var whatsAppConfigured = !string.IsNullOrWhiteSpace(configuration["WhatsApp:AccessToken"])
            && !string.IsNullOrWhiteSpace(configuration["WhatsApp:PhoneNumberId"]);

        if (whatsAppConfigured)
        {
            services.AddHttpClient<IWhatsAppProvider, MetaWhatsAppProvider>();
        }
        else
        {
            services.AddScoped<IWhatsAppProvider, DevWhatsAppProvider>();
        }

        services.AddScoped<ISmsProvider, DevSmsProvider>();

        return services;
    }
}
