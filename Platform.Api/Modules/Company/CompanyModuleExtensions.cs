using Platform.Api.Modules.Company.Services;

namespace Platform.Api.Modules.Company;

public static class CompanyModuleExtensions
{
    public static IServiceCollection AddCompanyModule(this IServiceCollection services)
    {
        services.AddScoped<ICompanyProfileService, CompanyProfileService>();
        services.AddScoped<IPortalAppearanceService, PortalAppearanceService>();
        return services;
    }
}
