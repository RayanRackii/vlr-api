using Platform.Api.Modules.CustomerAuth.PhoneVerification;
using Platform.Api.Modules.CustomerAuth.Services;
using Platform.Api.Services.Brazil;
using Platform.Core.Infrastructure.Persistence;

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
        services.AddMemoryCache();
        services.AddSingleton<IPhoneVerificationSendGate, PhoneVerificationSendGate>();
        services.AddScoped<ICustomerVerificationCodeService>(sp =>
            new CustomerVerificationCodeService(
                sp.GetRequiredService<AppDbContext>(),
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<PasswordResetEmailQueue>();
        services.AddSingleton<IPasswordResetEmailQueue>(sp =>
            sp.GetRequiredService<PasswordResetEmailQueue>());
        services.AddHostedService<PasswordResetEmailDispatcher>();
        services.AddScoped<ICustomerAuthService, CustomerAuthService>();
        services.AddHttpClient<IViaCepClient, ViaCepClient>(client =>
        {
            client.BaseAddress = new Uri("https://viacep.com.br/");
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        return services;
    }
}
