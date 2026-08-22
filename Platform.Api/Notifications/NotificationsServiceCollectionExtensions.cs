using Platform.Api.Notifications.Providers.Dev;
using Platform.Api.Notifications.Providers.Meta;
using Platform.Api.Notifications.Providers.Resend;

namespace Platform.Api.Notifications;

public static class NotificationsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the notification queue and dispatcher. Resend / Meta are
    /// registered only when <c>effectiveAllowExternal &amp;&amp; credentialsConfigured</c>;
    /// otherwise Dev providers are used (console log, no HTTP).
    /// </summary>
    public static IServiceCollection AddNotificationInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment hostEnvironment)
    {
        services.AddSingleton<NotificationQueue>();
        services.AddHostedService<NotificationDispatcherService>();

        services.Configure<NotificationsOptions>(configuration.GetSection(NotificationsOptions.SectionName));
        services.Configure<ResendOptions>(configuration.GetSection(ResendOptions.SectionName));
        services.Configure<MetaWhatsAppOptions>(configuration.GetSection(MetaWhatsAppOptions.SectionName));

        var allowExternalDelivery = configuration
            .GetSection(NotificationsOptions.SectionName)
            .Get<NotificationsOptions>()
            ?.AllowExternalDelivery;

        var effectiveAllowExternal = allowExternalDelivery
            ?? (hostEnvironment.IsDevelopment() ? false : true);

        var resendConfigured = !string.IsNullOrWhiteSpace(configuration["Resend:ApiKey"])
            && !string.IsNullOrWhiteSpace(configuration["Resend:FromEmail"]);

        if (effectiveAllowExternal && resendConfigured)
        {
            services.AddHttpClient<IEmailProvider, ResendEmailProvider>();
        }
        else
        {
            services.AddScoped<IEmailProvider, DevEmailProvider>();
        }

        var whatsAppConfigured = !string.IsNullOrWhiteSpace(configuration["WhatsApp:AccessToken"])
            && !string.IsNullOrWhiteSpace(configuration["WhatsApp:PhoneNumberId"]);

        if (effectiveAllowExternal && whatsAppConfigured)
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
