using Platform.Api.Modules.Admin.Services;

namespace Platform.Api.Modules.Admin;

public static class AdminModuleExtensions
{
    public static IServiceCollection AddAdminModule(this IServiceCollection services)
    {
        services.AddScoped<IAdminTenantService, AdminTenantService>();

        return services;
    }
}
