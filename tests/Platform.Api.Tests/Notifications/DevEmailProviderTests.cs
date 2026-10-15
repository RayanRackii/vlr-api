using Microsoft.Extensions.Logging;
using Platform.Api.Notifications.Providers.Dev;
using Platform.Api.Tests.Infrastructure;

namespace Platform.Api.Tests.Notifications;

public sealed class DevEmailProviderTests
{
    [Fact]
    public async Task Logs_recipient_and_subject_without_body_or_display_name()
    {
        using var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var provider = new DevEmailProvider(factory.CreateLogger<DevEmailProvider>());
        const string body = """
            <html>Olá Ana &lt;b&gt;. Total R$ 80,00.
            https://app.example.test/t/estudio/agenda?reservation=11111111-1111-4111-8111-111111111111
            token=invite-secret-token</html>
            """;

        await provider.SendAsync(
            "ana@estudio.test",
            "Convite Rolvix — Acme",
            body,
            "Estúdio Norte",
            CancellationToken.None);

        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("ana@estudio.test", entry.Message, StringComparison.Ordinal);
        Assert.Contains("Convite Rolvix — Acme", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("invite-secret-token", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("agenda?reservation", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Ana", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("R$ 80,00", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("<html>", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Estúdio Norte", entry.Message, StringComparison.Ordinal);
    }
}
