using Platform.Api.Modules.ResourcePlanning.Contracts;
using Platform.Api.Modules.ResourcePlanning.Services;

namespace Platform.Api.Modules.ResourcePlanning;

public static class ResourcePlanningModuleExtensions
{
    public static IServiceCollection AddResourcePlanningModule(this IServiceCollection services)
    {
        services.AddScoped<IPlanningProfileService, PlanningProfileService>();
        services.AddScoped<IAssetOccupancyService, AssetOccupancyService>();
        services.AddScoped<IPlanningCallerAccessor, PlanningCallerAccessor>();
        services.AddScoped<IAssetAllocationService, AssetAllocationService>();
        services.AddScoped<IPlanningAgendaService, PlanningAgendaService>();

        return services;
    }
}
