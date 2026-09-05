using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Notifications;

namespace Platform.Api.Modules.Catalog;

public static class CatalogModuleExtensions
{
    public static IServiceCollection AddCatalogModule(this IServiceCollection services)
    {
        services.AddScoped<ICatalogProductService, CatalogProductService>();
        services.AddScoped<ICatalogOrderService, CatalogOrderService>();
        services.AddScoped<ICatalogPortalService, CatalogPortalService>();
        services.AddScoped<ICatalogNotificationPublisher, CatalogNotificationPublisher>();
        services.AddScoped<ICatalogNotificationService, CatalogNotificationService>();
        services.AddScoped<INotificationOutboxProcessor, NotificationOutboxProcessor>();
        services.AddScoped<INotificationOutboxScheduler, HangfireNotificationOutboxScheduler>();

        return services;
    }
}
