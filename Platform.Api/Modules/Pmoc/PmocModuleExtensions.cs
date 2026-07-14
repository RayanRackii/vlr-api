using Platform.Api.Modules.Pmoc.Services;

namespace Platform.Api.Modules.Pmoc;

public static class PmocModuleExtensions
{
    public static IServiceCollection AddPmocModule(this IServiceCollection services)
    {
        services.AddScoped<IMaintenancePlanService, MaintenancePlanService>();
        services.AddScoped<IGlobalTemplateService, GlobalTemplateService>();

        return services;
    }
}
