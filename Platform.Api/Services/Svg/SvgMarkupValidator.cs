using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Platform.Api.Services.Svg;

/// <summary>
/// Validates tenant brand SVG markup before persistence. Rejects common XSS vectors;
/// normalizes root sizing so CSS controls display size. Frontend still sanitizes on render.
/// </summary>
public static class SvgMarkupValidator
{
    public const int MaxLength = 100_000;

    private static readonly Regex DangerousPattern = new(
        @"<script|</script|javascript:|data:text/html|foreignObject|\bon\w+\s*=|<iframe|<object|<embed|<link|<meta",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly XNamespace SvgNs = "http://www.w3.org/2000/svg";

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

        return MakeScalable(trimmed);
    }

    /// <summary>
    /// Ensures the root &lt;svg&gt; fills its CSS box: keep/derive viewBox, set width/height to 100%.
    /// Display size stays a frontend concern (hero vs sidebar).
    /// </summary>
    private static string MakeScalable(string svgMarkup)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(svgMarkup, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex)
        {
            throw new ArgumentException("LogoSvg is not well-formed XML.", ex);
        }

        var root = document.Root
            ?? throw new ArgumentException("LogoSvg must contain an <svg> root element.");

        if (!root.Name.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("LogoSvg must contain an <svg> root element.");
        }

        // Prefer SVG namespace on save so browsers treat it as SVG when inlined.
        if (root.Name.Namespace == XNamespace.None)
        {
            root.Name = SvgNs + "svg";
            foreach (var element in root.Descendants())
            {
                if (element.Name.Namespace == XNamespace.None)
                {
                    element.Name = SvgNs + element.Name.LocalName;
                }
            }
        }

        var viewBox = AttributeValue(root, "viewBox");
        if (string.IsNullOrWhiteSpace(viewBox))
        {
            var width = ParseLength(AttributeValue(root, "width"));
            var height = ParseLength(AttributeValue(root, "height"));
            if (width is > 0 && height is > 0)
            {
                root.SetAttributeValue(
                    "viewBox",
                    $"0 0 {FormatNumber(width.Value)} {FormatNumber(height.Value)}");
            }
        }

        root.SetAttributeValue("width", "100%");
        root.SetAttributeValue("height", "100%");
        root.SetAttributeValue("preserveAspectRatio", "xMidYMid meet");

        var normalized = root.ToString(SaveOptions.DisableFormatting);
        if (normalized.Length > MaxLength)
        {
            throw new ArgumentException(
                $"LogoSvg must be at most {MaxLength} characters after normalization.");
        }

        return normalized;
    }

    private static string? AttributeValue(XElement element, string localName)
    {
        return element.Attributes()
            .FirstOrDefault(a =>
                a.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
            ?.Value;
    }

    private static double? ParseLength(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        if (value.EndsWith('%'))
        {
            return null;
        }

        var numeric = Regex.Match(value, @"^[0-9]*\.?[0-9]+");
        if (!numeric.Success
            || !double.TryParse(
                numeric.Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)
            || parsed <= 0)
        {
            return null;
        }

        return parsed;
    }

    private static string FormatNumber(double value) =>
        value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
