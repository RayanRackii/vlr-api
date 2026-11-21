using Platform.Api.Modules.Billing.Services;
using Platform.Api.Services.Billing;

namespace Platform.Api.Modules.Billing;

public static class BillingModuleExtensions
{
    public static IServiceCollection AddBillingModule(this IServiceCollection services)
    {
        services.AddScoped<IPlanCatalogService, PlanCatalogService>();
        services.AddScoped<IAdminSubscriptionService, AdminSubscriptionService>();
        services.AddScoped<IAdminBillingSummaryService, AdminBillingSummaryService>();
        services.AddScoped<ITenantBillingReadService, TenantBillingReadService>();

        // Tenant staff billing and the provider webhook. The provider-facing services live under
        // Services/Billing (not in this module) so this module never references the Payments module directly.
        services.AddScoped<IPlanUsage, PlanUsageService>();
        services.AddScoped<IPlanModuleEnabler, PlanModuleEnabler>();
        services.AddScoped<ITenantBillingService, TenantBillingService>();
        services.AddScoped<IBillingWebhookIntake, BillingWebhookIntake>();
        services.AddScoped<IBillingWebhookJobScheduler, HangfireBillingWebhookJobScheduler>();
        services.AddScoped<IBillingWebhookProcessor, BillingWebhookProcessor>();
        return services;
    }
}
