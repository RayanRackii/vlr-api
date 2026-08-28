using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Platform.Api.Notifications;

namespace Platform.Api.Tests.Notifications;

public sealed class NotificationsServiceCollectionExtensionsTests
{
    [Theory]
    [InlineData("Development", true, null, "DevEmailProvider", "DevWhatsAppProvider")]
    [InlineData("Development", true, true, "ResendEmailProvider", "MetaWhatsAppProvider")]
    [InlineData("Development", true, false, "DevEmailProvider", "DevWhatsAppProvider")]
    [InlineData("Development", false, true, "DevEmailProvider", "DevWhatsAppProvider")]
    [InlineData("Production", true, null, "DevEmailProvider", "DevWhatsAppProvider")]
    [InlineData("Production", true, false, "DevEmailProvider", "DevWhatsAppProvider")]
    [InlineData("Production", false, null, "DevEmailProvider", "DevWhatsAppProvider")]
    [InlineData("Staging", true, null, "DevEmailProvider", "DevWhatsAppProvider")]
    public void AddNotificationInfrastructure_registers_providers_from_environment_credentials_and_flag(
        string environmentName,
        bool credentialsConfigured,
        bool? allowExternalDelivery,
        string expectedEmailProvider,
        string expectedWhatsAppProvider)
    {
        using var provider = BuildProvider(environmentName, credentialsConfigured, allowExternalDelivery);

        Assert.Equal(expectedEmailProvider, provider.GetRequiredService<IEmailProvider>().GetType().Name);
        Assert.Equal(expectedWhatsAppProvider, provider.GetRequiredService<IWhatsAppProvider>().GetType().Name);
    }

    private static ServiceProvider BuildProvider(
        string environmentName,
        bool credentialsConfigured,
        bool? allowExternalDelivery)
    {
        var values = new Dictionary<string, string?>();
        if (credentialsConfigured)
        {
            values["Resend:ApiKey"] = "re_test_not_a_real_key";
            values["Resend:FromEmail"] = "dev@rolvix.test";
            values["WhatsApp:AccessToken"] = "test-access-token";
            values["WhatsApp:PhoneNumberId"] = "000000000000000";
        }

        if (allowExternalDelivery is { } flag)
        {
            values["Notifications:AllowExternalDelivery"] = flag ? "true" : "false";
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddNotificationInfrastructure(configuration, new FakeHostEnvironment(environmentName));
        return services.BuildServiceProvider();
    }

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Platform.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
