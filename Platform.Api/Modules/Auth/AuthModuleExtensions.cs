using Platform.Api.Modules.Auth.Services;

namespace Platform.Api.Modules.Auth;

public static class AuthModuleExtensions
{
    public static IServiceCollection AddAuthModule(this IServiceCollection services)
    {
        services.AddScoped<IPasswordRecoveryService, PasswordRecoveryService>();
        return services;
    }
}
