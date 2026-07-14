using Platform.Api.Modules.WorkOrders.Services;

namespace Platform.Api.Modules.WorkOrders;

public static class WorkOrdersModuleExtensions
{
    public static IServiceCollection AddWorkOrdersModule(this IServiceCollection services)
    {
        services.AddScoped<IWorkOrderService, WorkOrderService>();

        return services;
    }
}
