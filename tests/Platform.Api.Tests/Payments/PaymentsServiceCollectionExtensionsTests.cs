using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Modules.Payments;
using Platform.Api.Modules.Payments.Gateways;
using Platform.Api.Modules.Payments.Security;

namespace Platform.Api.Tests.Payments;

public sealed class PaymentsServiceCollectionExtensionsTests
{
    [Fact]
    public void Without_any_payments_configuration_the_services_resolve_and_fail_only_at_use()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPaymentsModule(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider(validateScopes: true);

        var protector = provider.GetRequiredService<IPaymentSecretProtector>();
        var gateway = provider.GetRequiredService<IPaymentGateway>();

        Assert.NotNull(gateway);
        Assert.Throws<PaymentConfigurationException>(() => protector.Protect("x", Guid.NewGuid()));
    }

    [Fact]
    public void Versioned_keys_bind_from_configuration()
    {
        var key = Convert.ToBase64String(new byte[32]);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Payments:EncryptionKeys:v1"] = key })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddLogging();
        services.AddPaymentsModule(configuration);
        using var provider = services.BuildServiceProvider();

        var protector = provider.GetRequiredService<IPaymentSecretProtector>();
        var tenant = Guid.NewGuid();

        Assert.Equal("secret", protector.Unprotect(protector.Protect("secret", tenant), tenant));
    }
}
