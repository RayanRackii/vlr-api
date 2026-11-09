using System.Net;
using System.Text.RegularExpressions;
using Platform.Api.Notifications;

namespace Platform.Api.Tests.Notifications;

public sealed class RolvixEmailLayoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Wrap_dark_palette_has_contrasting_surfaces_text_codes_links_and_actions(bool outlook)
    {
        var html = RolvixEmailLayout.Wrap("Ana", RolvixEmailLayout.InviteBody("https://example.invalid/invite", "Clube"));
        Assert.Contains("name=\"color-scheme\" content=\"light dark\"", html);
        Assert.Contains("name=\"supported-color-schemes\" content=\"light dark\"", html);
        var css = Regex.Match(html, @"<style>(.*?)</style>", RegexOptions.Singleline).Groups[1].Value;
        if (!outlook)
            css = Regex.Match(css, @"@media \(prefers-color-scheme: dark\) \{(.*?)\n\s*\}", RegexOptions.Singleline).Groups[1].Value;
        var prefix = outlook ? "[data-ogsc] " : string.Empty;

        string Rule(string selector)
        {
            var match = Regex.Matches(css, @"([^{}]+)\{([^{}]+)\}")
                .Cast<Match>().Single(m => m.Groups[1].Value.Split(',')
                    .Any(s => s.Trim() == prefix + selector));
            return match.Groups[2].Value;
        }
        string Color(string selector, string property) => Regex.Match(Rule(selector),
            $@"(?:^|;)\s*{Regex.Escape(property)}:(#[0-9a-f]{{6}}) !important;").Groups[1].Value;

        var page = Color(".email-page", "background");
        var surface = Color(".email-content", "background");
        Assert.Equal("#121212", page);
        Assert.Equal(page, Color(".email-background", "background"));
        Assert.Equal("#202024", surface);
        Assert.Equal(surface, Color(".email-card", "background"));
        Assert.Equal("#52525b", Color(".email-card", "border-color"));
        AssertContrast(Color(".email-page", "color"), page);
        AssertContrast(Color(".email-content", "color"), surface);
        foreach (var inlineColor in new[] { "#27272a", "#18181b", "#71717a" })
            AssertContrast(Color($".email-content [style*=\"color:{inlineColor}\"]", "color"), surface);
        AssertContrast(Color(".email-content a", "color"), surface);
        const string action = ".email-content a[style*=\"background:#000000\"]";
        AssertContrast(Color(action, "color"), Color(action, "background"));
        AssertContrast(Color(".email-chrome", "color"), Color(".email-chrome", "background"));
        AssertContrast(Color(".email-chrome span", "color"), Color(".email-chrome", "background"));
        Assert.Contains("class=\"email-content\" bgcolor=\"#ffffff\"", html);
        Assert.Contains("class=\"email-page\" bgcolor=\"#f4f4f5\"", html);
        Assert.Contains(EmailStyles.Button, html);
        Assert.Contains(EmailStyles.Text, html);
    }

    private static void AssertContrast(string foreground, string background)
    {
        static double Luminance(string hex)
        {
            Assert.Matches("^#[0-9a-f]{6}$", hex);
            double Channel(int start)
            {
                var value = Convert.ToInt32(hex.Substring(start, 2), 16) / 255d;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
        }
        var a = Luminance(foreground);
        var b = Luminance(background);
        var ratio = (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        Assert.True(ratio >= 4.5, $"{foreground} on {background}: contrast {ratio:F2} must be at least 4.5:1.");
    }

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
