using System.Net;
using System.Text.RegularExpressions;
using Platform.Api.Notifications;

namespace Platform.Api.Tests.Notifications;

public sealed class RolvixEmailLayoutTests
{
    [Fact]
    public void Wrap_invite_body_has_no_paragraph_tags()
    {
        var html = RolvixEmailLayout.Wrap(
            "Ana",
            RolvixEmailLayout.InviteBody("https://rolvix.com.br/invite?token=abc", "Clube"));

        Assert.DoesNotContain("<p", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Wrap_invite_body_encodes_recipient_and_company()
    {
        var html = RolvixEmailLayout.Wrap(
            "Ana <script>alert(1)</script>",
            RolvixEmailLayout.InviteBody(
                "https://rolvix.com.br/invite?token=abc",
                "Clube <script>"));

        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(WebUtility.HtmlEncode("Ana <script>alert(1)</script>"), html, StringComparison.Ordinal);
        Assert.Contains(WebUtility.HtmlEncode("Clube <script>"), html, StringComparison.Ordinal);
    }

    [Fact]
    public void Invite_body_contains_cta_and_encoded_href()
    {
        const string inviteUrl = "https://rolvix.com.br/invite?token=a&b=<script>";
        var html = RolvixEmailLayout.Wrap(
            "Ana",
            RolvixEmailLayout.InviteBody(inviteUrl, "Clube"));

        Assert.Contains("Aceitar convite", html, StringComparison.Ordinal);
        Assert.Contains("não altera senha", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"{WebUtility.HtmlEncode(inviteUrl)}\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Invite_body_defaults_keep_the_current_panel_and_portal_text()
    {
        var html = RolvixEmailLayout.InviteBody("https://rolvix.com.br/invite?token=abc", "Clube");

        Assert.Contains("<strong>rolvix.com.br</strong> (agenda e configurações). O site público (<em>seu-subdominio</em>.rolvix.com.br)", html, StringComparison.Ordinal);
        Assert.Contains("<strong>https://rolvix.com.br/login</strong>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Invite_body_uses_the_resolved_frontend_base_url_and_portal_template()
    {
        var html = RolvixEmailLayout.InviteBody(
            "https://dev.example.test/invite?token=abc",
            "Clube",
            frontendBaseUrl: "https://dev.example.test",
            tenantPortalHostTemplate: "dev.example.test/t/{subdomain}");

        Assert.Contains("<strong>dev.example.test</strong>", html, StringComparison.Ordinal);
        Assert.Contains("dev.example.test/t/<em>seu-subdominio</em>", html, StringComparison.Ordinal);
        Assert.Contains("<strong>https://dev.example.test/login</strong>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("rolvix.com.br", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Wrap_invite_body_has_no_paragraph_as_direct_row_child()
    {
        var html = RolvixEmailLayout.Wrap(
            "Ana",
            RolvixEmailLayout.InviteBody("https://rolvix.com.br/invite?token=abc", "Clube"));

        Assert.DoesNotMatch(new Regex(@"<tr>\s*<p", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), html);
    }

    [Fact]
    public void Recovery_body_has_no_paragraph_tags()
    {
        var html = RolvixEmailLayout.RecoveryBody("https://rolvix.com.br/reset-password?token_hash=abc&type=recovery");

        Assert.DoesNotContain("<p", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Email_verification_body_has_no_paragraph_tags()
    {
        var html = RolvixEmailLayout.Wrap(
            "Ana",
            RolvixEmailLayout.EmailVerificationBody("847291"));

        Assert.DoesNotContain("<p", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("847291", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Password_reset_body_has_no_paragraph_tags_and_states_15_minutes()
    {
        var html = RolvixEmailLayout.Wrap(
            "Ana",
            RolvixEmailLayout.PasswordResetBody("847291"));

        Assert.Equal("Redefinição de senha Rolvix", RolvixEmailLayout.PasswordResetSubject);
        Assert.DoesNotContain("<p", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("847291", html, StringComparison.Ordinal);
        Assert.Contains("Este código expira em 15 minutos.", html, StringComparison.Ordinal);
        Assert.Contains("Sua senha permanece a mesma.", html, StringComparison.Ordinal);
    }
}
