using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Platform.Core.Infrastructure.ResourceVisuals;

/// <summary>Small structural SVG vocabulary. Unknown markup is rejected instead of silently retained.</summary>
public static partial class ResourceSvgSanitizer
{
    public const int MaxLayerBytes = 100 * 1024;
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly HashSet<string> Elements = ["svg", "g", "defs", "pattern", "rect", "circle", "ellipse", "line", "polyline", "polygon", "path"];
    private static readonly HashSet<string> NumericAttributes = ["x", "y", "width", "height", "rx", "ry", "cx", "cy", "r", "x1", "x2", "y1", "y2", "stroke-width", "stroke-miterlimit", "stroke-dashoffset"];
    private static readonly HashSet<string> PaintAttributes = ["fill", "stroke"];
    private static readonly HashSet<string> NamedColors = ["none", "black", "white", "transparent"];

    public static string NormalizeViewBox(string value)
    {
        var numbers = ParseNumbers(value, 4);
        if (numbers.Length != 4 || numbers[2] <= 0 || numbers[3] <= 0) throw Unsafe("Invalid viewBox.");
        return string.Join(" ", numbers.Select(x => x.ToString("G17", CultureInfo.InvariantCulture)));
    }

    public static string Sanitize(string source, string expectedViewBox, string idPrefix = "")
    {
        if (string.IsNullOrWhiteSpace(source) || Encoding.UTF8.GetByteCount(source) > MaxLayerBytes) throw Unsafe("SVG layer exceeds its size limit or is empty.");
        XDocument document;
        try
        {
            using var input = new StringReader(source);
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxLayerBytes, IgnoreComments = true, IgnoreProcessingInstructions = false });
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException) { throw Unsafe("Invalid SVG XML."); }
        if (document.Nodes().Any(x => x is XProcessingInstruction or XDocumentType)) throw Unsafe("Processing instructions are prohibited.");
        var root = document.Root ?? throw Unsafe("SVG root required.");
        if (root.Name != Svg + "svg" || root.Attribute("viewBox") == null) throw Unsafe("SVG namespace and viewBox required.");
        var expected = NormalizeViewBox(expectedViewBox);
        if (NormalizeViewBox(root.Attribute("viewBox")!.Value) != expected) throw Unsafe("Every layer must share the declared viewBox.");
        var elements = root.DescendantsAndSelf().ToArray();
        if (elements.Length > 2000) throw Unsafe("Too many SVG elements.");
        var ids = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var references = new List<(XElement Element, XAttribute Attribute, string Id)>();
        foreach (var element in elements)
        {
            if (element.Name.Namespace != Svg || !Elements.Contains(element.Name.LocalName) || (element != root && element.Name.LocalName == "svg")) throw Unsafe("SVG element is not allowed.");
            if (element.Ancestors().Count() >= 32) throw Unsafe("SVG nesting limit exceeded.");
            if (element.Nodes().Any(n => n is not XElement && n is not XText || n is XText t && !string.IsNullOrWhiteSpace(t.Value))) throw Unsafe("SVG text and instructions are prohibited.");
            foreach (var attribute in element.Attributes().ToArray())
            {
                var name = attribute.Name.LocalName;
                var value = attribute.Value.Trim();
                if (element == root && name is "x" or "y" or "width" or "height")
                    throw Unsafe("SVG root geometry is defined by viewBox only.");
                if (attribute.IsNamespaceDeclaration)
                {
                    if (element != root || name != "xmlns" || value != Svg.NamespaceName) throw Unsafe("Additional namespaces are prohibited.");
                    continue;
                }
                if (attribute.Name.Namespace != XNamespace.None) throw Unsafe("Namespaced attributes are prohibited.");
                if (name == "id")
                {
                    if (!IdPattern().IsMatch(value) || !ids.TryAdd(value, element)) throw Unsafe("Invalid or duplicate SVG ID.");
                }
                else if (name == "viewBox" && element == root) attribute.Value = expected;
                else if (NumericAttributes.Contains(name))
                {
                    var number = ParseNumbers(value, 1);
                    if (number.Length != 1 || (name is "width" or "height" or "r" or "rx" or "ry" or "stroke-width" && number[0] < 0)) throw Unsafe("Invalid SVG dimension.");
                }
                else if (name is "opacity" or "fill-opacity" or "stroke-opacity")
                {
                    var number = ParseNumbers(value, 1);
                    if (number.Length != 1 || number[0] < 0 || number[0] > 1) throw Unsafe("Invalid SVG opacity.");
                }
                else if (PaintAttributes.Contains(name))
                {
                    var match = InternalReference().Match(value);
                    if (match.Success) references.Add((element, attribute, match.Groups[1].Value));
                    else if (!HexColor().IsMatch(value) && !NamedColors.Contains(value)) throw Unsafe("Only plain colors and internal patterns are allowed.");
                }
                else if (name == "d")
                {
                    if (!PathPattern().IsMatch(value) || value.Length > 50000) throw Unsafe("Invalid SVG path.");
                    ValidateEmbeddedNumbers(value);
                }
                else if (name is "points" or "stroke-dasharray") ParseNumbers(value, 4000);
                else if (name == "patternUnits" && element.Name.LocalName == "pattern" && value is "userSpaceOnUse" or "objectBoundingBox") { }
                else if (name == "stroke-linecap" && value is "butt" or "round" or "square") { }
                else if (name == "stroke-linejoin" && value is "miter" or "round" or "bevel") { }
                else if (name == "fill-rule" && value is "nonzero" or "evenodd") { }
                else if (name == "preserveAspectRatio" && element == root && value is "xMidYMid meet" or "none") { }
                else throw Unsafe("SVG attribute is not allowed.");
            }
        }
        foreach (var reference in references)
        {
            if (!ids.TryGetValue(reference.Id, out var target) || target.Name.LocalName != "pattern" || reference.Element.AncestorsAndSelf().Any(e => e.Name.LocalName == "pattern")) throw Unsafe("Invalid or recursive internal pattern reference.");
            reference.Attribute.Value = $"url(#{idPrefix}{reference.Id})";
        }
        foreach (var pair in ids) pair.Value.SetAttributeValue("id", idPrefix + pair.Key);
        return root.ToString(SaveOptions.DisableFormatting);
    }

    public static string Compose(string viewBox, string floor, string? lines, string? net)
    {
        var root = new XElement(Svg + "svg", new XAttribute("viewBox", NormalizeViewBox(viewBox)));
        foreach (var (name, source) in new[] { ("floor", floor), ("lines", lines), ("net", net) })
        {
            if (source == null) continue;
            var sanitized = XElement.Parse(Sanitize(source, viewBox, name + "-"));
            var presentation = sanitized.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.LocalName is not ("viewBox" or "preserveAspectRatio" or "x" or "y" or "width" or "height"));
            var group = new XElement(Svg + "g", presentation, sanitized.Elements());
            if (group.Attribute("id") == null) group.SetAttributeValue("id", name);
            root.Add(group);
        }
        return root.ToString(SaveOptions.DisableFormatting);
    }

    private static double[] ParseNumbers(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || !NumericList().IsMatch(value)) throw Unsafe("Invalid SVG numeric value.");
        var tokens = value.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || tokens.Length > maximum) throw Unsafe("SVG numeric limit exceeded.");
        return tokens.Select(token => double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) && Math.Abs(number) <= 10000000 ? number : throw Unsafe("SVG numbers must be finite and bounded.")).ToArray();
    }
    private static void ValidateEmbeddedNumbers(string value)
    {
        foreach (Match match in NumberPattern().Matches(value))
            if (!double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || Math.Abs(number) > 10000000) throw Unsafe("Invalid path coordinate.");
    }
    private static ArgumentException Unsafe(string message) => new(message);
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]{0,79}$")] private static partial Regex IdPattern();
    [GeneratedRegex("^#[0-9a-fA-F]{6}$")] private static partial Regex HexColor();
    [GeneratedRegex("^url\\(#([A-Za-z_][A-Za-z0-9_-]{0,79})\\)$")] private static partial Regex InternalReference();
    [GeneratedRegex("^[0-9eE+.,\\s-]+$")] private static partial Regex NumericList();
    [GeneratedRegex("^[MmZzLlHhVvCcSsQqTtAa0-9eE+.,\\s-]+$")] private static partial Regex PathPattern();
    [GeneratedRegex("[-+]?(?:[0-9]+(?:\\.[0-9]*)?|\\.[0-9]+)(?:[eE][-+]?[0-9]+)?")] private static partial Regex NumberPattern();
}
