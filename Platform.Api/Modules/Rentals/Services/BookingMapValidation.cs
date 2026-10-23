using System.Text.Json;
using System.Text.RegularExpressions;
using Platform.Api.Modules.Rentals.Dtos;

namespace Platform.Api.Modules.Rentals.Services;

public sealed class BookingMapException(string code, string message, int statusCode = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public static partial class BookingMapValidation
{
    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex ColorPattern();
    [GeneratedRegex("^[a-zA-Z0-9_-]{1,80}$", RegexOptions.CultureInvariant)]
    private static partial Regex IconPattern();
    public static bool IsColor(string? value) => value is not null && ColorPattern().IsMatch(value);
    public static void Canvas(string name, double aspect, double width)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(char.IsControl)
            || !double.IsFinite(aspect) || aspect < 0.1 || aspect > 10
            || !double.IsFinite(width) || width < 50 || width > 100)
            throw Invalid("Invalid map name or canvas dimensions.");
    }
    public static void Rectangle(double x, double y, double width, double height, short rotation)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height)
            || x < 0 || y < 0 || width <= 0 || height <= 0 || width > 100 || height > 100
            || x + width > 100.000000001 || y + height > 100.000000001
            || rotation is not (0 or 90 or 180 or 270))
            throw Invalid("Map geometry must be finite, positive and inside the canvas; rotation must be 0, 90, 180 or 270.");
    }
    public static (string Style, string? Geometry) Decoration(SaveBookingMapDecorationRequest d)
    {
        Rectangle(d.XPercent, d.YPercent, d.WidthPercent, d.HeightPercent, d.Rotation);
        if (d.Kind is not ("Walkway" or "Label" or "Landmark" or "Shape")
            || d.Label?.Length > 120 || d.Label?.Any(char.IsControl) == true
            || d.Label?.IndexOfAny(['<', '>']) >= 0
            || (d.IconKey is not null && !IconPattern().IsMatch(d.IconKey)))
            throw Invalid("Invalid decoration kind, label or icon.");
        var style = d.Style.ValueKind == JsonValueKind.Undefined ? JsonSerializer.SerializeToElement(new { }) : d.Style;
        if (style.ValueKind != JsonValueKind.Object || style.GetRawText().Length > 2048)
            throw Invalid("Decoration style must be a bounded object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in style.EnumerateObject())
        {
            if (!names.Add(p.Name)) throw Invalid("Duplicate decoration style property.");
            switch (p.Name)
            {
                case "fill": case "stroke":
                    if (p.Value.ValueKind != JsonValueKind.String || !IsColor(p.Value.GetString()))
                        throw Invalid("Decoration colors must use #RRGGBB.");
                    break;
                case "strokeWidth": case "opacity":
                    if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetDouble(out var number) || !double.IsFinite(number) || number < 0
                        || number > (p.Name == "opacity" ? 1 : 10)) throw Invalid("Invalid decoration numeric style.");
                    break;
                default: throw Invalid("Unsupported decoration style property.");
            }
        }
        string? geometry = null;
        if (d.Geometry is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } g)
        {
            if (g.ValueKind != JsonValueKind.Object || g.GetRawText().Length > 8192
                || g.EnumerateObject().Count() != 1 || !g.TryGetProperty("points", out var points)
                || points.ValueKind != JsonValueKind.Array || points.GetArrayLength() is < 2 or > 64)
                throw Invalid("Geometry accepts only 2 to 64 numeric points.");
            foreach (var p in points.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object || p.EnumerateObject().Count() != 2
                    || !p.TryGetProperty("x", out var px) || !p.TryGetProperty("y", out var py)
                    || px.ValueKind != JsonValueKind.Number || py.ValueKind != JsonValueKind.Number
                    || !px.TryGetDouble(out var x) || !py.TryGetDouble(out var y)
                    || !double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x > 100 || y > 100)
                    throw Invalid("Geometry points must be numeric coordinates between 0 and 100.");
            }
            geometry = g.GetRawText();
        }
        return (style.GetRawText(), geometry);
    }
    public static BookingMapException Invalid(string message) => new("MAP_INVALID", message);
    public static BookingMapException Missing() => new("MAP_NOT_FOUND", "Map was not found.", 404);
    public static BookingMapException Conflict() => new("MAP_VERSION_CONFLICT", "Map settings or composition changed; reload before saving.", 409);
}
