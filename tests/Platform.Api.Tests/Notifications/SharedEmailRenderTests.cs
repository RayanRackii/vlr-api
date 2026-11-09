using System.Text.Json;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Notifications;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Notifications;

public sealed class SharedEmailRenderTests
{
    public static IEnumerable<object[]> Templates()
    {
        const string link = "https://example.invalid/action?sample=synthetic&view=email";
        foreach (var (name, roles, portal) in new[]
        {
            ("invite-staff", new[] { "Operator" }, "tenant.example.invalid"),
            ("invite-admin", new[] { "Admin" }, "tenant.example.invalid"),
            ("invite-multiple-roles", new[] { "Admin", "Operator" }, "tenant.example.invalid"),
            ("invite-no-portal", Array.Empty<string>(), (string?)null),
        })
        {
            var html = RolvixEmailLayout.Wrap("Pessoa Exemplo", RolvixEmailLayout.InviteBody(
                link, "Empresa Exemplo", portal, roleNames: roles,
                frontendBaseUrl: "https://example.invalid", tenantPortalHostTemplate: "{subdomain}.example.invalid"), "Empresa Exemplo");
            yield return new object[] { name, EmailContent.FromRenderedHtml(html), "Aceitar convite" };
        }
        yield return Html("invite-legacy", RolvixEmailLayout.InviteBody(link, "sua empresa",
            frontendBaseUrl: "https://example.invalid", tenantPortalHostTemplate: "{subdomain}.example.invalid"), "Aceitar convite");
        yield return Html("staff-recovery", RolvixEmailLayout.RecoveryBody(link), "Redefinir senha");
        yield return Html("customer-verification", RolvixEmailLayout.EmailVerificationBody("000000"), "10 minutos");
        yield return Html("customer-password-reset", RolvixEmailLayout.PasswordResetBody("000000"), "15 minutos");
        foreach (var eventType in RentalEventTypes.Notifying)
        {
            var rendered = ReservationCustomerEmail.Render(eventType, new Dictionary<string, string?>
            {
                [ReservationCustomerEmail.TenantNameKey] = "Empresa Exemplo",
                [ReservationCustomerEmail.CustomerNameKey] = "Pessoa Exemplo",
                [ReservationCustomerEmail.ReferenceKey] = "EXEMPLO-001",
                [ReservationCustomerEmail.DateKey] = "09/11/2026",
                [ReservationCustomerEmail.StartKey] = "10:00",
                [ReservationCustomerEmail.EndKey] = "11:00",
                [ReservationCustomerEmail.ItemsKey] = "Recurso Exemplo",
                [ReservationCustomerEmail.TotalKey] = "R$ 100,00",
                [ReservationCustomerEmail.DepositKey] = "R$ 20,00",
                [ReservationCustomerEmail.UrlKey] = link,
            });
            yield return new object[] { eventType, new EmailContent(rendered.HtmlBody, rendered.TextBody), "EXEMPLO-001" };
        }
        foreach (var seed in CatalogNotificationTemplates.Seeds.Where(s => s.Channel == NotificationChannel.Email))
        {
            var text = seed.Body.Replace("{{customerName}}", "Pessoa Exemplo")
                .Replace("{{orderNumber}}", "EXEMPLO-001")
                .Replace("{{rejectionReason}}", "Motivo de exemplo")
                .Replace("{{cancellationReason}}", "Motivo de exemplo");
            yield return new object[] { seed.EventType, EmailContent.FromPlainText(text, "Empresa Exemplo"), "EXEMPLO-001" };
        }
        yield return new object[] { "generic-custom", EmailContent.FromPlainText("Aviso <script>exemplo</script> & conteúdo\nhttps://example.invalid/details"), "Aviso" };
    }

    private static object[] Html(string name, string body, string expected) =>
        new object[] { name, EmailContent.FromRenderedHtml(RolvixEmailLayout.Wrap("Pessoa Exemplo", body)), expected };

    [Theory]
    [MemberData(nameof(Templates))]
    public async Task Every_template_has_shared_chrome_and_both_provider_alternatives(string name, EmailContent content, string expected)
    {
        Assert.Contains("<!DOCTYPE html>", content.Html);
        Assert.Contains("<!--[if mso]>", content.Html);
        Assert.Contains("bgcolor=\"#ffffff\"", content.Html);
        Assert.Contains("name=\"color-scheme\" content=\"light dark\"", content.Html);
        Assert.Contains("@media (prefers-color-scheme: dark)", content.Html);
        Assert.Contains("[data-ogsc] .email-content", content.Html);
        Assert.Contains("background:#202024 !important;color:#f4f4f5 !important", content.Html);
        Assert.Contains(expected, content.Html);
        Assert.Contains(expected, content.Text);
        Assert.DoesNotContain("<script>", content.Html);
        Assert.DoesNotContain("<table", content.Text);
        Assert.DoesNotContain("{{", content.Text);
        var handler = new ResendEmailProviderTests.CaptureHandler();
        using var client = new HttpClient(handler);
        await ResendEmailProviderTests.CreateProvider(client).SendAsync("recipient@example.invalid", name, content);
        using var json = JsonDocument.Parse(handler.Payload!);
        Assert.Equal(content.Html, json.RootElement.GetProperty("html").GetString());
        Assert.Equal(content.Text, json.RootElement.GetProperty("text").GetString());
        Assert.Equal(name, json.RootElement.GetProperty("subject").GetString());
        Assert.Equal("recipient@example.invalid", json.RootElement.GetProperty("to")[0].GetString());
    }

    [Fact]
    public void Export_synthetic_previews()
    {
        var root = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Platform.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var directory = Path.Combine(root.FullName, "artifacts", "email-layout");
        Directory.CreateDirectory(directory);
        var names = new List<string>();
        foreach (var row in Templates())
        {
            var name = (string)row[0];
            var content = (EmailContent)row[1];
            Assert.DoesNotContain("rolvix.com.br", content.Html);
            File.WriteAllText(Path.Combine(directory, name + ".html"), content.Html);
            File.WriteAllText(Path.Combine(directory, name + ".txt"), content.Text);
            names.Add(name);
        }
        File.WriteAllText(Path.Combine(directory, "index.html"), "<!DOCTYPE html><html lang=\"pt-BR\"><meta charset=\"utf-8\"><title>Emails sintéticos</title><body><h1>Emails sintéticos</h1><ul>"
            + string.Join("", names.Select(n => $"<li><a href=\"{n}.html\">{n}</a> · <a href=\"{n}.txt\">texto</a></li>")) + "</ul></body></html>");
        Assert.Equal(20, names.Count);
    }
}
