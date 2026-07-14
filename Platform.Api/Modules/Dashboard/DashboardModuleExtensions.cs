using Platform.Api.Modules.Dashboard.Services;

namespace Platform.Api.Modules.Dashboard;

public static class DashboardModuleExtensions
{
    public static IServiceCollection AddDashboardModule(this IServiceCollection services)
    {
        services.AddScoped<IDashboardService, DashboardService>();

        return services;
    }
}
