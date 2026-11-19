using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Api.Modules.Assets.Services;

/// <summary>
/// Validates and projects asset attribute dictionaries against a family's field schema JSON.
/// </summary>
public static class AssetFamilyAttributeValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static Dictionary<string, string?> ValidateAndProject(
        string fieldSchemaJson,
        IReadOnlyDictionary<string, string?>? incoming)
    {
        var schema = ParseSchema(fieldSchemaJson);
        var source = incoming ?? new Dictionary<string, string?>(StringComparer.Ordinal);
        var projected = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var field in schema.Fields)
        {
            source.TryGetValue(field.Key, out var raw);
            var normalized = NormalizeValue(field, raw);

            if (field.Required && string.IsNullOrWhiteSpace(normalized))
            {
                throw new ArgumentException(
                    $"Attribute '{field.Key}' is required for this asset family.");
            }

            if (normalized is not null)
            {
                projected[field.Key] = normalized;
            }
        }

        return projected;
    }

    /// <summary>
    /// Update variant: null <paramref name="incoming"/> keeps <paramref name="existing"/>; a provided
    /// dictionary replaces it. A required field only fails when it ends up blank AND was not blank in
    /// the stored values, so assets that predate a newly required field stay editable.
    /// </summary>
    public static Dictionary<string, string?> ValidateAndProjectForUpdate(
        string fieldSchemaJson,
        IReadOnlyDictionary<string, string?>? incoming,
        IReadOnlyDictionary<string, string?>? existing)
    {
        var schema = ParseSchema(fieldSchemaJson);
        var keepStored = incoming is null;
        var source = incoming ?? existing ?? new Dictionary<string, string?>(StringComparer.Ordinal);
        var projected = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var field in schema.Fields)
        {
            source.TryGetValue(field.Key, out var raw);

            if (keepStored)
            {
                // Nothing was submitted: carry the stored value over untouched, even if it no longer
                // parses under the current field type, so legacy data never blocks an edit.
                if (!string.IsNullOrWhiteSpace(raw)) projected[field.Key] = raw.Trim();
                continue;
            }

            var normalized = NormalizeValue(field, raw);

            if (field.Required && string.IsNullOrWhiteSpace(normalized) && HadValue(existing, field.Key))
            {
                throw new ArgumentException(
                    $"Attribute '{field.Key}' is required for this asset family.");
            }

            if (normalized is not null)
            {
                projected[field.Key] = normalized;
            }
        }

        return projected;
    }

    private static bool HadValue(IReadOnlyDictionary<string, string?>? existing, string key) =>
        existing is not null
        && existing.TryGetValue(key, out var value)
        && !string.IsNullOrWhiteSpace(value);

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

            return parsed ?? new AssetFieldSchemaDto([]);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "Asset family field schema is invalid JSON.",
                ex);
        }
    }

    private static string? NormalizeValue(AssetFieldDefinitionDto field, string? raw)
    {
        if (raw is null || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();

        return field.Type.ToLowerInvariant() switch
        {
            "number" => NormalizeNumber(field.Key, trimmed),
            "boolean" => NormalizeBoolean(field.Key, trimmed),
            "text" => trimmed,
            _ => trimmed,
        };
    }

    private static string NormalizeNumber(string key, string value)
    {
        if (!decimal.TryParse(
                value,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out _))
        {
            throw new ArgumentException($"Attribute '{key}' must be a number.");
        }

        return value;
    }

    private static string NormalizeBoolean(string key, string value)
    {
        if (bool.TryParse(value, out var parsed))
        {
            return parsed ? "true" : "false";
        }

        if (value is "1" or "0")
        {
            return value == "1" ? "true" : "false";
        }

        throw new ArgumentException($"Attribute '{key}' must be a boolean.");
    }
}

public sealed record AssetFieldSchemaDto(
    [property: JsonPropertyName("fields")] IReadOnlyList<AssetFieldDefinitionDto> Fields);

public sealed record AssetFieldDefinitionDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("required")] bool Required = false,
    [property: JsonPropertyName("label")] string? Label = null);
