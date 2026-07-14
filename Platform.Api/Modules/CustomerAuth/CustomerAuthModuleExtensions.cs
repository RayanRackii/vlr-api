using Platform.Api.Modules.CustomerAuth.Services;

namespace Platform.Api.Modules.CustomerAuth;

public static class CustomerAuthModuleExtensions
{
    public static IServiceCollection AddCustomerAuthModule(this IServiceCollection services)
    {
        services.AddScoped<ICustomerAuthService, CustomerAuthService>();

        return services;
    }
}
