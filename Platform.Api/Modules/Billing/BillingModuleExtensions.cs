using Platform.Api.Modules.Billing.Services;

namespace Platform.Api.Modules.Billing;

public static class BillingModuleExtensions
{
    public static IServiceCollection AddBillingModule(this IServiceCollection services)
    {
        services.AddScoped<IPlanCatalogService, PlanCatalogService>();
        services.AddScoped<IAdminSubscriptionService, AdminSubscriptionService>();
        services.AddScoped<IAdminBillingSummaryService, AdminBillingSummaryService>();
        return services;
    }
}
