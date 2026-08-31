using Platform.Api.Modules.CustomerAuth.PhoneVerification;
using Platform.Api.Modules.CustomerAuth.Services;
using Platform.Api.Services.Brazil;

namespace Platform.Api.Modules.CustomerAuth;

public static class CustomerAuthModuleExtensions
{
    public static IServiceCollection AddCustomerAuthModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<TwilioVerifyOptions>(configuration.GetSection(TwilioVerifyOptions.SectionName));
        services.AddHttpClient<IPhoneVerificationClient, TwilioVerifyPhoneVerificationClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddScoped<ICustomerAuthService, CustomerAuthService>();
        services.AddHttpClient<IViaCepClient, ViaCepClient>(client =>
        {
            client.BaseAddress = new Uri("https://viacep.com.br/");
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        return services;
    }
}
