using Platform.Api.Notifications.Providers.Dev;
using Platform.Api.Notifications.Providers.Meta;
using Platform.Api.Notifications.Providers.Resend;

namespace Platform.Api.Notifications;

public static class NotificationsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the notification queue and dispatcher. Resend / Meta are
    /// registered only when the resolved channel gate is true and credentials
    /// exist; otherwise Dev providers are used (console log, no HTTP).
    /// </summary>
    public static IServiceCollection AddNotificationInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment hostEnvironment)
    {
        ArgumentNullException.ThrowIfNull(hostEnvironment);

        services.AddSingleton<NotificationQueue>();
        services.AddHostedService<NotificationDispatcherService>();

        services.Configure<NotificationsOptions>(configuration.GetSection(NotificationsOptions.SectionName));
        services.Configure<ResendOptions>(configuration.GetSection(ResendOptions.SectionName));
        services.Configure<MetaWhatsAppOptions>(configuration.GetSection(MetaWhatsAppOptions.SectionName));

        var options = configuration
            .GetSection(NotificationsOptions.SectionName)
            .Get<NotificationsOptions>()
            ?? new NotificationsOptions();

        var emailGate = ExternalDeliveryResolution.IsEnabled(
            options.AllowExternalEmail,
            options.AllowExternalDelivery);
        var whatsAppGate = ExternalDeliveryResolution.IsEnabled(
            options.AllowExternalWhatsApp,
            options.AllowExternalDelivery);

        var resendConfigured = !string.IsNullOrWhiteSpace(configuration["Resend:ApiKey"])
            && !string.IsNullOrWhiteSpace(configuration["Resend:FromEmail"]);

        var emailExternal = emailGate && resendConfigured;
        if (emailExternal)
        {
            services.AddHttpClient<IEmailProvider, ResendEmailProvider>();
        }
        else
        {
            services.AddScoped<IEmailProvider, DevEmailProvider>();
        }

        var whatsAppConfigured = !string.IsNullOrWhiteSpace(configuration["WhatsApp:AccessToken"])
            && !string.IsNullOrWhiteSpace(configuration["WhatsApp:PhoneNumberId"]);

        var whatsAppExternal = whatsAppGate && whatsAppConfigured;
        if (whatsAppExternal)
        {
            services.AddHttpClient<IWhatsAppProvider, MetaWhatsAppProvider>();
        }
        else
        {
            services.AddScoped<IWhatsAppProvider, DevWhatsAppProvider>();
        }

        services.AddScoped<ISmsProvider, DevSmsProvider>();
        services.AddHostedService(sp => new NotificationDeliveryGateHostedService(
            emailExternal,
            whatsAppExternal,
            hostEnvironment.IsProduction(),
            emailGate,
            resendConfigured,
            sp.GetRequiredService<ILogger<NotificationDeliveryGateHostedService>>()));

        return services;
    }
}

internal sealed class NotificationDeliveryGateHostedService(
    bool emailEnabled,
    bool whatsAppEnabled,
    bool isProduction,
    bool emailGate,
    bool resendConfigured,
    ILogger<NotificationDeliveryGateHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "External email delivery {State}",
            emailEnabled ? "enabled" : "disabled");
        logger.LogInformation(
            "External WhatsApp delivery {State}",
            whatsAppEnabled ? "enabled" : "disabled");

        if (isProduction && !emailEnabled)
        {
            logger.LogError(
                "DevEmailProvider is selected in Production. Invite and recovery mail will not reach Resend until Notifications:AllowExternalEmail is true and Resend credentials are present.");
        }

        if (emailGate && !resendConfigured)
        {
            logger.LogError(
                "Resend configuration is incomplete (missing ApiKey or FromEmail). Email stays on DevEmailProvider.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
