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
        using var provider = BuildProvider(
            environmentName,
            credentialsConfigured,
            allowExternalDelivery);

        Assert.Equal(expectedEmailProvider, provider.GetRequiredService<IEmailProvider>().GetType().Name);
        Assert.Equal(expectedWhatsAppProvider, provider.GetRequiredService<IWhatsAppProvider>().GetType().Name);
        Assert.Equal("DevSmsProvider", provider.GetRequiredService<ISmsProvider>().GetType().Name);
    }

    [Fact]
    public void All_unset_uses_dev_providers()
    {
        using var provider = BuildProvider("Production", credentialsConfigured: true);

        AssertProviders(provider, "DevEmailProvider", "DevWhatsAppProvider");
    }

    [Fact]
    public void Global_true_only_is_legacy_behavior_both_external()
    {
        using var provider = BuildProvider(
            "Production",
            credentialsConfigured: true,
            allowExternalDelivery: true);

        AssertProviders(provider, "ResendEmailProvider", "MetaWhatsAppProvider");
    }

    [Fact]
    public void Global_false_and_whatsapp_true_uses_meta_and_dev_email()
    {
        using var provider = BuildProvider(
            "Production",
            credentialsConfigured: true,
            allowExternalDelivery: false,
            allowExternalEmail: null,
            allowExternalWhatsApp: true);

        AssertProviders(provider, "DevEmailProvider", "MetaWhatsAppProvider");
    }

    [Fact]
    public void Unset_global_email_false_whatsapp_true_uses_meta_and_dev_email()
    {
        using var provider = BuildProvider(
            "Production",
            credentialsConfigured: true,
            allowExternalDelivery: null,
            allowExternalEmail: false,
            allowExternalWhatsApp: true);

        AssertProviders(provider, "DevEmailProvider", "MetaWhatsAppProvider");
    }

    [Fact]
    public void Global_true_and_email_false_keeps_email_dev()
    {
        using var provider = BuildProvider(
            "Production",
            credentialsConfigured: true,
            allowExternalDelivery: true,
            allowExternalEmail: false);

        AssertProviders(provider, "DevEmailProvider", "MetaWhatsAppProvider");
    }

    [Fact]
    public void Email_true_and_whatsapp_false_uses_resend_only()
    {
        using var provider = BuildProvider(
            "Production",
            credentialsConfigured: true,
            allowExternalEmail: true,
            allowExternalWhatsApp: false);

        AssertProviders(provider, "ResendEmailProvider", "DevWhatsAppProvider");
    }

    [Fact]
    public void Explicit_channel_false_overrides_global_true()
    {
        using var provider = BuildProvider(
            "Production",
            credentialsConfigured: true,
            allowExternalDelivery: true,
            allowExternalEmail: false,
            allowExternalWhatsApp: false);

        AssertProviders(provider, "DevEmailProvider", "DevWhatsAppProvider");
    }

    [Fact]
    public void Explicit_channel_true_overrides_global_false()
    {
        using var provider = BuildProvider(
            "Production",
            credentialsConfigured: true,
            allowExternalDelivery: false,
            allowExternalEmail: true,
            allowExternalWhatsApp: true);

        AssertProviders(provider, "ResendEmailProvider", "MetaWhatsAppProvider");
    }

    [Fact]
    public void Credentials_missing_fail_closed_even_when_gates_true()
    {
        using var provider = BuildProvider(
            "Production",
            credentialsConfigured: false,
            allowExternalDelivery: true,
            allowExternalEmail: true,
            allowExternalWhatsApp: true);

        AssertProviders(provider, "DevEmailProvider", "DevWhatsAppProvider");
    }

    [Fact]
    public void WhatsApp_gate_true_without_whatsapp_credentials_stays_dev()
    {
        var values = new Dictionary<string, string?>
        {
            ["Resend:ApiKey"] = "re_test_not_a_real_key",
            ["Resend:FromEmail"] = "dev@rolvix.test",
            ["Notifications:AllowExternalEmail"] = "true",
            ["Notifications:AllowExternalWhatsApp"] = "true",
        };

        using var provider = BuildProviderFromValues("Production", values);

        AssertProviders(provider, "ResendEmailProvider", "DevWhatsAppProvider");
    }

    [Fact]
    public void Email_gate_true_without_resend_credentials_stays_dev()
    {
        var values = new Dictionary<string, string?>
        {
            ["WhatsApp:AccessToken"] = "test-access-token",
            ["WhatsApp:PhoneNumberId"] = "000000000000000",
            ["Notifications:AllowExternalEmail"] = "true",
            ["Notifications:AllowExternalWhatsApp"] = "true",
        };

        using var provider = BuildProviderFromValues("Production", values);

        AssertProviders(provider, "DevEmailProvider", "MetaWhatsAppProvider");
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, null, true)]
    [InlineData(false, null, false)]
    public void IsEnabled_channel_overrides_global_and_unset_is_false(
        bool? channel,
        bool? global,
        bool expected)
    {
        Assert.Equal(expected, ExternalDeliveryResolution.IsEnabled(channel, global));
    }

    private static void AssertProviders(
        ServiceProvider provider,
        string expectedEmail,
        string expectedWhatsApp)
    {
        Assert.Equal(expectedEmail, provider.GetRequiredService<IEmailProvider>().GetType().Name);
        Assert.Equal(expectedWhatsApp, provider.GetRequiredService<IWhatsAppProvider>().GetType().Name);
        Assert.Equal("DevSmsProvider", provider.GetRequiredService<ISmsProvider>().GetType().Name);
    }

    private static ServiceProvider BuildProvider(
        string environmentName,
        bool credentialsConfigured,
        bool? allowExternalDelivery = null,
        bool? allowExternalEmail = null,
        bool? allowExternalWhatsApp = null)
    {
        var values = new Dictionary<string, string?>();
        if (credentialsConfigured)
        {
            values["Resend:ApiKey"] = "re_test_not_a_real_key";
            values["Resend:FromEmail"] = "dev@rolvix.test";
            values["WhatsApp:AccessToken"] = "test-access-token";
            values["WhatsApp:PhoneNumberId"] = "000000000000000";
        }

        if (allowExternalDelivery is { } global)
        {
            values["Notifications:AllowExternalDelivery"] = global ? "true" : "false";
        }

        if (allowExternalEmail is { } email)
        {
            values["Notifications:AllowExternalEmail"] = email ? "true" : "false";
        }

        if (allowExternalWhatsApp is { } whatsApp)
        {
            values["Notifications:AllowExternalWhatsApp"] = whatsApp ? "true" : "false";
        }

        return BuildProviderFromValues(environmentName, values);
    }

    private static ServiceProvider BuildProviderFromValues(
        string environmentName,
        Dictionary<string, string?> values)
    {
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
