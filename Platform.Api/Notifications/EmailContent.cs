using System.Net;
using System.Text.RegularExpressions;

namespace Platform.Api.Notifications;

/// <summary>Explicit alternatives. HTML must come from platform renderers, never stored/user markup.</summary>
public sealed record EmailContent(string Html, string Text)
{
    public static EmailContent FromPlainText(string text, string? brandName = null) => new(
        RolvixEmailLayout.Wrap(string.Empty,
            $"<div style=\"word-break:break-word;{EmailStyles.Text}\">{EmailHtml.Encode(text).Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "<br />")}</div>",
            brandName), text);

    /// <summary>Derives a readable alternative only from platform-owned, already encoded HTML.</summary>
    public static EmailContent FromRenderedHtml(string html)
    {
        var text = Regex.Replace(html, @"<head\b[^>]*>.*?</head>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<br\s*/?>|</(?:tr|div|p|table)>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        text = WebUtility.HtmlDecode(text);
        text = string.Join("\n", text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));
        return new(html, text);
    }
}
