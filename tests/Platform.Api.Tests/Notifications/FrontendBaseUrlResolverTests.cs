using Microsoft.Extensions.Configuration;
using Platform.Api.Notifications;

namespace Platform.Api.Tests.Notifications;

public sealed class FrontendBaseUrlResolverTests
{
    [Fact]
    public void Resolve_tenant_portal_host_uses_the_default_template()
    {
        var configuration = new ConfigurationBuilder().Build();

        var host = FrontendBaseUrlResolver.ResolveTenantPortalHost(configuration, "Estudio");

        Assert.Equal("estudio.rolvix.com.br", host);
    }

    [Fact]
    public void Resolve_tenant_portal_host_uses_the_configured_template()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:TenantPortalHostTemplate"] = "dev.example.test/t/{subdomain}",
            })
            .Build();

        var host = FrontendBaseUrlResolver.ResolveTenantPortalHost(configuration, "Estudio");

        Assert.Equal("dev.example.test/t/estudio", host);
    }
}
