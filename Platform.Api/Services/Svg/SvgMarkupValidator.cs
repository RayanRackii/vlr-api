using System.Text.RegularExpressions;

namespace Platform.Api.Services.Svg;

/// <summary>
/// Validates tenant brand SVG markup before persistence. Rejects common XSS vectors;
/// the frontend still sanitizes on render.
/// </summary>
public static class SvgMarkupValidator
{
    public const int MaxLength = 100_000;

    private static readonly Regex DangerousPattern = new(
        @"<script|</script|javascript:|data:text/html|foreignObject|\bon\w+\s*=|<iframe|<object|<embed|<link|<meta",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length > MaxLength)
        {
            throw new ArgumentException(
                $"LogoSvg must be at most {MaxLength} characters.");
        }

        var start = trimmed.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            throw new ArgumentException("LogoSvg must contain an <svg> root element.");
        }

        trimmed = trimmed[start..];

        var end = trimmed.LastIndexOf("</svg>", StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            throw new ArgumentException("LogoSvg must be a complete </svg> document fragment.");
        }

        trimmed = trimmed[..(end + "</svg>".Length)];

        if (DangerousPattern.IsMatch(trimmed))
        {
            throw new ArgumentException(
                "LogoSvg contains disallowed markup (scripts, handlers, or embeds).");
        }

        return trimmed;
    }
}
