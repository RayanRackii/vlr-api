using Platform.Api.Modules.Admin.Services;
using Platform.Api.Services.Tenancy;

namespace Platform.Api.Modules.Admin;

public static class AdminModuleExtensions
{
    public static IServiceCollection AddAdminModule(this IServiceCollection services)
    {
        services.AddScoped<ITenantProvisioner, TenantProvisioner>();
        services.AddScoped<ITenantPurger, TenantPurger>();
        services.AddScoped<IAdminTenantService, AdminTenantService>();
        services.AddScoped<ITenantUserAdminService, TenantUserAdminService>();
        services.AddScoped<IPlatformUserAdminService, PlatformUserAdminService>();
        services.AddScoped<IPlatformAdminMembershipService, PlatformAdminMembershipService>();

        return services;
    }
}
