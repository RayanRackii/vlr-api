using Platform.Api.Modules.Roles.Services;

namespace Platform.Api.Modules.Roles;

public static class RolesModuleExtensions
{
    public static IServiceCollection AddRolesModule(this IServiceCollection services)
    {
        services.AddScoped<IRoleService, RoleService>();
        return services;
    }
}
