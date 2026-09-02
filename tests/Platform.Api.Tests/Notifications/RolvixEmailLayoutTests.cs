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

        Assert.Contains("Definir senha", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"{WebUtility.HtmlEncode(inviteUrl)}\"", html, StringComparison.Ordinal);
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
}
