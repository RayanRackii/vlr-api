using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Platform.Api.Modules.Assets.Services;

/// <summary>
/// Validates and projects typed asset attribute values against a family's field schema JSON.
/// Values are stored as JSON: number as JSON number, boolean as JSON boolean, multiselect as a
/// JSON array of strings and every other type as a JSON string. Strings are still accepted for
/// every type (old clients, scripts, legacy rows).
/// </summary>
public static class AssetFamilyAttributeValidator
{
    public const int MaxAttributeKeys = 200;
    private const int DefaultTextMaxLength = 500;
    private const int DefaultLongTextMaxLength = 5000;
    private const int DefaultDecimals = 6;
    private const int MaxUrlLength = 2048;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Regex NumberPattern = new(
        @"^-?\d+(\.\d+)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// Validates <paramref name="incoming"/> against the schema and returns the dictionary to store.
    /// </summary>
    /// <param name="existing">
    /// Stored values of the asset being updated; null on create, bulk create, or when the asset
    /// changes family.
    /// </param>
    /// <param name="isUpdate">
    /// True for updates. A null <paramref name="incoming"/> then keeps the stored values untouched
    /// (no validation); a required field only fails when the asset already had a value for it.
    /// </param>
    public static Dictionary<string, JsonElement> ValidateAndProject(
        string fieldSchemaJson,
        IReadOnlyDictionary<string, JsonElement>? incoming,
        IReadOnlyDictionary<string, JsonElement>? existing,
        bool isUpdate)
    {
        var schema = ParseSchema(fieldSchemaJson);

        if (incoming is not null && incoming.Count > MaxAttributeKeys)
        {
            throw new ArgumentException($"Attributes must have at most {MaxAttributeKeys} entries.");
        }

        if (isUpdate && incoming is null)
        {
            // Nothing was submitted: carry the stored values over verbatim, even if they no longer
            // satisfy the current field definitions, so legacy data never blocks an edit.
            return CloneAll(existing);
        }

        var activeFields = schema.Fields
            .Where(f => !f.Archived)
            .GroupBy(f => f.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        // Rule 1: values under keys that are not active fields (archived, unknown) are never dropped.
        if (existing is not null)
        {
            foreach (var (key, value) in existing)
            {
                if (!activeFields.ContainsKey(key) && !IsAbsent(value))
                {
                    result[key] = value.Clone();
                }
            }
        }

        foreach (var field in activeFields.Values)
        {
            JsonElement? resolved = null;
            JsonElement existingValue = default;
            var hadExisting = existing is not null
                && existing.TryGetValue(field.Key, out existingValue)
                && !IsAbsent(existingValue);

            if (incoming is not null
                && incoming.TryGetValue(field.Key, out var raw)
                && !IsAbsent(raw))
            {
                resolved = ResolveIncoming(field, raw, hadExisting ? existingValue : null);
            }

            if (resolved is { } value)
            {
                result[field.Key] = value;
            }
            else if (field.Required && (!isUpdate || hadExisting))
            {
                throw new ArgumentException(
                    $"Attribute '{field.Key}' is required for this asset family.");
            }
        }

        return result;
    }

    private static JsonElement? ResolveIncoming(
        AssetFieldDefinitionDto field,
        JsonElement raw,
        JsonElement? existing)
    {
        if (!TryCoerce(field, raw, out var coerced))
        {
            if (existing is { } stored && JsonElement.DeepEquals(raw, stored))
            {
                // Untouched legacy value that no longer coerces: keep it verbatim.
                return stored.Clone();
            }

            throw new ArgumentException(InvalidShapeMessage(field));
        }

        if (IsAbsent(coerced))
        {
            return null;
        }

        if (existing is { } previous
            && TryCoerce(field, previous, out var coercedPrevious)
            && JsonElement.DeepEquals(coerced, coercedPrevious))
        {
            // Untouched legacy value: tolerate options or limits that were tightened later and
            // upgrade "256" to 256.
            return coerced;
        }

        if (!TryConstrain(field, coerced, out var canonical, out var error))
        {
            throw new ArgumentException(error);
        }

        return canonical;
    }

    private static Dictionary<string, JsonElement> CloneAll(IReadOnlyDictionary<string, JsonElement>? source)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (source is null)
        {
            return result;
        }

        foreach (var (key, value) in source)
        {
            if (!IsAbsent(value))
            {
                result[key] = value.Clone();
            }
        }

        return result;
    }

    /// <summary>Null, Undefined, blank string and empty array count as "absent".</summary>
    public static bool IsAbsent(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()),
        JsonValueKind.Array => value.GetArrayLength() == 0,
        _ => false,
    };

    /// <summary>
    /// Shape-only conversion of <paramref name="raw"/> to the representation of the field type.
    /// Not called for absent values.
    /// </summary>
    public static bool TryCoerce(AssetFieldDefinitionDto field, JsonElement raw, out JsonElement value)
    {
        value = default;
        var kind = raw.ValueKind;

        switch (NormalizeType(field.Type))
        {
            case "number":
                return TryCoerceNumber(raw, out value);

            case "boolean":
                if (kind is JsonValueKind.True or JsonValueKind.False)
                {
                    value = raw.Clone();
                    return true;
                }

                if (kind == JsonValueKind.String)
                {
                    var text = raw.GetString()!.Trim();
                    if (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1")
                    {
                        value = JsonSerializer.SerializeToElement(true);
                        return true;
                    }

                    if (text.Equals("false", StringComparison.OrdinalIgnoreCase) || text == "0")
                    {
                        value = JsonSerializer.SerializeToElement(false);
                        return true;
                    }
                }

                return false;

            case "multiselect":
                {
                    List<string> tokens;
                    if (kind == JsonValueKind.Array)
                    {
                        tokens = [];
                        foreach (var item in raw.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.String)
                            {
                                return false;
                            }

                            tokens.Add(item.GetString()!);
                        }
                    }
                    else if (kind == JsonValueKind.String)
                    {
                        tokens = raw.GetString()!.Split(',').ToList();
                    }
                    else
                    {
                        return false;
                    }

                    var distinct = tokens
                        .Select(t => t.Trim())
                        .Where(t => t.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    value = JsonSerializer.SerializeToElement(distinct);
                    return true;
                }

            case "longtext":
                return TryCoerceText(raw, normalizeNewlines: true, out value);

            case "select":
            case "url":
            case "date":
                if (kind != JsonValueKind.String)
                {
                    return false;
                }

                value = JsonSerializer.SerializeToElement(raw.GetString()!.Trim());
                return true;

            default:
                return TryCoerceText(raw, normalizeNewlines: false, out value);
        }
    }

    /// <summary>
    /// Applies the field constraints to an already coerced value and returns its canonical form.
    /// </summary>
    public static bool TryConstrain(
        AssetFieldDefinitionDto field,
        JsonElement coerced,
        out JsonElement canonical,
        out string? error)
    {
        canonical = coerced;
        error = null;
        var key = field.Key;

        switch (NormalizeType(field.Type))
        {
            case "number":
                {
                    var number = coerced.GetDecimal();
                    var decimals = field.Decimals ?? DefaultDecimals;
                    if (field.Min is { } min && number < min
                        || field.Max is { } max && number > max)
                    {
                        error = field switch
                        {
                            { Min: { } lo, Max: { } hi } =>
                                $"Attribute '{key}' must be between {Format(lo)} and {Format(hi)}.",
                            { Min: { } lo } => $"Attribute '{key}' must be at least {Format(lo)}.",
                            _ => $"Attribute '{key}' must be at most {Format(field.Max!.Value)}.",
                        };
                        return false;
                    }

                    if (ScaleOf(number) > decimals)
                    {
                        error = $"Attribute '{key}' must have at most {decimals} decimal places.";
                        return false;
                    }

                    return true;
                }

            case "boolean":
                return true;

            case "select":
                {
                    var option = FindOption(field, coerced.GetString()!);
                    if (option is null)
                    {
                        error = $"Attribute '{key}' must be one of the listed options.";
                        return false;
                    }

                    canonical = JsonSerializer.SerializeToElement(option);
                    return true;
                }

            case "multiselect":
                {
                    var chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in coerced.EnumerateArray())
                    {
                        var option = FindOption(field, item.GetString()!);
                        if (option is null)
                        {
                            error = $"Attribute '{key}' must be one of the listed options.";
                            return false;
                        }

                        chosen.Add(option);
                    }

                    var ordered = (field.Options ?? []).Where(chosen.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    canonical = JsonSerializer.SerializeToElement(ordered);
                    return true;
                }

            case "url":
                {
                    var text = coerced.GetString()!;
                    if (text.Length > MaxUrlLength
                        || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
                        || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    {
                        error = $"Attribute '{key}' must be an http or https URL.";
                        return false;
                    }

                    return true;
                }

            case "date":
                if (!DateOnly.TryParseExact(
                        coerced.GetString()!,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out _))
                {
                    error = $"Attribute '{key}' must be a date in yyyy-MM-dd format.";
                    return false;
                }

                return true;

            case "longtext":
                return CheckLength(field, coerced.GetString()!, DefaultLongTextMaxLength, out error);

            default:
                return CheckLength(field, coerced.GetString()!, DefaultTextMaxLength, out error);
        }
    }

    public static AssetFieldSchemaDto ParseSchema(string fieldSchemaJson)
    {
        if (string.IsNullOrWhiteSpace(fieldSchemaJson))
        {
            return new AssetFieldSchemaDto([]);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<AssetFieldSchemaDto>(
                fieldSchemaJson,
                JsonOptions);

            return parsed is null
                ? new AssetFieldSchemaDto([])
                : parsed with { Fields = parsed.Fields ?? [] };
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Asset family field schema is invalid.", ex);
        }
    }

    private static string NormalizeType(string? type) => (type ?? string.Empty).Trim().ToLowerInvariant();

    private static string InvalidShapeMessage(AssetFieldDefinitionDto field) =>
        NormalizeType(field.Type) switch
        {
            "number" => $"Attribute '{field.Key}' must be a number.",
            "boolean" => $"Attribute '{field.Key}' must be a boolean.",
            "multiselect" => $"Attribute '{field.Key}' must be a list of options.",
            "select" => $"Attribute '{field.Key}' must be one of the listed options.",
            "url" => $"Attribute '{field.Key}' must be an http or https URL.",
            "date" => $"Attribute '{field.Key}' must be a date in yyyy-MM-dd format.",
            _ => $"Attribute '{field.Key}' must be text.",
        };

    private static bool TryCoerceNumber(JsonElement raw, out JsonElement value)
    {
        value = default;
        decimal number;

        if (raw.ValueKind == JsonValueKind.Number)
        {
            if (!raw.TryGetDecimal(out number))
            {
                return false;
            }
        }
        else if (raw.ValueKind == JsonValueKind.String)
        {
            var text = raw.GetString()!.Trim();
            if (!NumberPattern.IsMatch(text)
                || !decimal.TryParse(
                    text,
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out number))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        value = JsonSerializer.SerializeToElement(Normalize(number));
        return true;
    }

    private static bool TryCoerceText(JsonElement raw, bool normalizeNewlines, out JsonElement value)
    {
        value = default;
        string text;

        switch (raw.ValueKind)
        {
            case JsonValueKind.String:
                text = raw.GetString()!;
                break;
            case JsonValueKind.Number:
                text = raw.GetRawText();
                break;
            case JsonValueKind.True:
                text = "true";
                break;
            case JsonValueKind.False:
                text = "false";
                break;
            case JsonValueKind.Array:
                var parts = new List<string>();
                foreach (var item in raw.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        return false;
                    }

                    parts.Add(item.GetString()!);
                }

                text = string.Join(", ", parts);
                break;
            default:
                return false;
        }

        if (normalizeNewlines)
        {
            text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        value = JsonSerializer.SerializeToElement(text.Trim());
        return true;
    }

    private static bool CheckLength(
        AssetFieldDefinitionDto field,
        string text,
        int defaultMax,
        out string? error)
    {
        var max = field.MaxLength ?? defaultMax;
        if (text.Length > max)
        {
            error = $"Attribute '{field.Key}' must be at most {max} characters.";
            return false;
        }

        error = null;
        return true;
    }

    private static string? FindOption(AssetFieldDefinitionDto field, string candidate) =>
        field.Options?.FirstOrDefault(o => string.Equals(o, candidate, StringComparison.OrdinalIgnoreCase));

    private static decimal Normalize(decimal value) => value / 1.0000000000000000000000000000m;

    private static int ScaleOf(decimal value) => (decimal.GetBits(value)[3] >> 16) & 0xFF;

    private static string Format(decimal value) => Normalize(value).ToString(CultureInfo.InvariantCulture);
}

public sealed record AssetFieldSchemaDto(
    [property: JsonPropertyName("fields")] IReadOnlyList<AssetFieldDefinitionDto> Fields,
    [property: JsonPropertyName("version")] int Version = 1);

public sealed record AssetFieldDefinitionDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("required")] bool Required = false,
    [property: JsonPropertyName("label")] string? Label = null,
    [property: JsonPropertyName("help")] string? Help = null,
    [property: JsonPropertyName("unit")] string? Unit = null,
    [property: JsonPropertyName("min")] decimal? Min = null,
    [property: JsonPropertyName("max")] decimal? Max = null,
    [property: JsonPropertyName("decimals")] int? Decimals = null,
    [property: JsonPropertyName("options")] IReadOnlyList<string>? Options = null,
    [property: JsonPropertyName("maxLength")] int? MaxLength = null,
    [property: JsonPropertyName("group")] string? Group = null,
    [property: JsonPropertyName("archived")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool Archived = false);
