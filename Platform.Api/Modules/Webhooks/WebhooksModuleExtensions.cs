using Platform.Api.Modules.Webhooks.Services;

namespace Platform.Api.Modules.Webhooks;

public static class WebhooksModuleExtensions
{
    public static IServiceCollection AddWebhooksModule(this IServiceCollection services)
    {
        services.AddScoped<IWhatsAppWebhookProcessor, WhatsAppWebhookProcessor>();
        return services;
    }
}
