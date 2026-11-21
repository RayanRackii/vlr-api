using Platform.Api.Modules.Payments.Gateways;
using Platform.Api.Modules.Payments.Gateways.Asaas;
using Platform.Api.Modules.Payments.Security;

namespace Platform.Api.Modules.Payments;

public static class PaymentsServiceCollectionExtensions
{
    public static IServiceCollection AddPaymentsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PaymentsOptions>(configuration.GetSection(PaymentsOptions.SectionName));
        services.Configure<AsaasOptions>(configuration.GetSection(AsaasOptions.SectionName));

        // Nothing here reads a key or a URL: missing configuration fails at use, so environments without payments boot.
        services.AddSingleton<IPaymentSecretProtector, PaymentSecretProtector>();
        services.AddSingleton<IBillingWebhookTokenValidator, AsaasBillingWebhookTokenValidator>();

        services.AddHttpClient<IPaymentGateway, AsaasGateway>(client => client.Timeout = AsaasGateway.RequestTimeout)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        // The platform's own billing (root account) is the same adapter behind its own contract.
        services.AddHttpClient<IPlatformBillingGateway, AsaasGateway>(client => client.Timeout = AsaasGateway.RequestTimeout)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        return services;
    }
}
