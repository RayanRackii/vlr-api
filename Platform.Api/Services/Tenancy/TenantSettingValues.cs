using System.Text.Json;

namespace Platform.Api.Services.Tenancy;

/// <summary>Parsing and canonical formatting of tenant setting values (stored as text).</summary>
public static class TenantSettingValues
{
    /// <summary>Accepts exactly "true" or "false" (any case, surrounding spaces ignored).</summary>
    public static bool TryParseBoolean(string? raw, out bool value)
    {
        value = false;
        return raw is not null && bool.TryParse(raw.Trim(), out value);
    }

    /// <summary>
    /// Parses a JSON array of non-empty GUID strings. Anything else (null, blank, not an array,
    /// a non-string element, a bad or empty GUID) fails. An empty array parses to an empty list.
    /// </summary>
    public static bool TryParseRentalAssetIds(string? raw, out IReadOnlyList<Guid> ids)
    {
        ids = [];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var parsed = new List<Guid>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String
                    || !Guid.TryParse(element.GetString(), out var id)
                    || id == Guid.Empty)
                {
                    return false;
                }

                parsed.Add(id);
            }

            ids = parsed.Distinct().ToList();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Canonical stored form: a JSON array of lowercase GUID strings, sorted, no duplicates.</summary>
    public static string FormatRentalAssetIds(IEnumerable<Guid> ids) =>
        JsonSerializer.Serialize(
            ids.Distinct().Select(id => id.ToString("D")).Order(StringComparer.Ordinal).ToArray());
}
