using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Api.DomainEvents;
using Platform.Api.Modules.Catalog.Services;
using Platform.Core.Domain.Events;

namespace Platform.Api.Modules.Catalog;

public static class CatalogModuleExtensions
{
    public static IServiceCollection AddCatalogModule(this IServiceCollection services)
    {
        services.AddScoped<CatalogPolicyService>();
        services.AddScoped<CatalogTaxonomyOptionsService>();
        services.AddScoped<ICatalogProductService, CatalogProductService>();
        services.AddScoped<ICatalogOrderService, CatalogOrderService>();
        services.AddScoped<ICatalogPortalService, CatalogPortalService>();
        services.AddScoped<ICatalogNotificationPublisher, CatalogNotificationPublisher>();
        services.AddScoped<ICatalogProductResourceRequirementService, CatalogProductResourceRequirementService>();
        services.AddScoped<ICatalogOrderAllocationService, CatalogOrderAllocationService>();
        services.AddScoped<ICatalogOrderAllocationPreviewService, CatalogOrderAllocationPreviewService>();
        services.AddScoped<ICatalogOrderAllocationUsageService, CatalogOrderAllocationUsageService>();
        services.AddScoped<ICatalogAgendaService, CatalogAgendaService>();
        services.TryAddScoped(typeof(IDomainEventDispatcher<>), typeof(DomainEventDispatcher<>));
        services.AddScoped<IDomainEventHandler<CatalogOrderEvent>>(sp =>
            sp.GetRequiredService<ICatalogNotificationPublisher>());
        services.AddScoped<ICatalogNotificationService, CatalogNotificationService>();

        return services;
    }
}
