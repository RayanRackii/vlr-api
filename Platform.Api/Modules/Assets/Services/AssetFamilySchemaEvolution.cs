using System.Text.Json;
using System.Text.Json.Serialization;
using Platform.Api.Modules.Assets.Dtos;

namespace Platform.Api.Modules.Assets.Services;

/// <summary>
/// Pure planner for asset family schema changes. It never touches the database: the caller loads
/// the stored values and the planner decides, per field key, what a save would do and whether any
/// stored value would be left incompatible with the new definition.
/// </summary>
public static class AssetFamilySchemaEvolution
{
    public const string Unchanged = "unchanged";
    public const string Added = "added";
    public const string Updated = "updated";
    public const string TypeChanged = "typeChanged";
    public const string Archived = "archived";
    public const string Unarchived = "unarchived";
    public const string Removed = "removed";

    private const int MaxSamples = 5;
    private const int MaxSampleLength = 40;

    internal static readonly JsonSerializerOptions SchemaJsonOptions = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <param name="stored">The schema currently stored on the family.</param>
    /// <param name="requested">The requested definitions, already definition-validated.</param>
    /// <param name="assets">Tenant and stored attribute values of every asset of the family (all tenants).</param>
    public static AssetFamilySchemaPlan Plan(
        AssetFieldSchemaDto stored,
        IReadOnlyList<AssetFieldDefinitionDto> requested,
        IReadOnlyList<(Guid TenantId, IReadOnlyDictionary<string, JsonElement> Attributes)> assets)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(assets);

        var storedByKey = new Dictionary<string, AssetFieldDefinitionDto>(StringComparer.Ordinal);
        foreach (var field in stored.Fields)
        {
            storedByKey.TryAdd(field.Key, field);
        }

        var held = new Dictionary<string, List<(Guid TenantId, JsonElement Value)>>(StringComparer.Ordinal);
        foreach (var (tenantId, attributes) in assets)
        {
            foreach (var (key, value) in attributes)
            {
                if (AssetFamilyAttributeValidator.IsAbsent(value))
                {
                    continue;
                }

                if (!held.TryGetValue(key, out var list))
                {
                    list = [];
                    held[key] = list;
                }

                list.Add((tenantId, value));
            }
        }

        var final = new List<AssetFieldDefinitionDto>(requested);
        var impacts = new List<AssetFamilyFieldImpactDto>();
        var messages = new List<string>();
        var requestedKeys = new HashSet<string>(requested.Select(f => f.Key), StringComparer.Ordinal);

        foreach (var field in requested)
        {
            storedByKey.TryGetValue(field.Key, out var previous);
            held.TryGetValue(field.Key, out var values);
            var impact = Evaluate(field, previous, values ?? [], out var message);
            impacts.Add(impact);
            if (message is not null)
            {
                messages.Add(message);
            }
        }

        foreach (var previous in stored.Fields)
        {
            if (!requestedKeys.Add(previous.Key))
            {
                continue;
            }

            held.TryGetValue(previous.Key, out var values);
            var count = values?.Count ?? 0;
            var tenants = values?.Select(v => v.TenantId).Distinct().Count() ?? 0;

            string change;
            if (count > 0)
            {
                // Values are never dropped by a schema change: keep the definition, archived.
                final.Add(previous with { Archived = true, Required = false });
                change = previous.Archived ? Unchanged : Archived;
            }
            else
            {
                change = Removed;
            }

            impacts.Add(new AssetFamilyFieldImpactDto(
                previous.Key, change, count, tenants, 0, [], 0, false));
        }

        return new AssetFamilySchemaPlan(
            final,
            impacts,
            messages,
            assets.Count,
            assets.Select(a => a.TenantId).Distinct().Count());
    }

    /// <summary>Canonical JSON of a field list; two lists with the same text are the same schema.</summary>
    public static string SerializeFields(IReadOnlyList<AssetFieldDefinitionDto> fields) =>
        JsonSerializer.Serialize(fields, SchemaJsonOptions);

    /// <summary>
    /// Order-insensitive for archived fields: active fields in order, then archived fields by key.
    /// A save whose signature equals the stored one is a no-op and must not bump the version.
    /// </summary>
    public static string Signature(IReadOnlyList<AssetFieldDefinitionDto> fields)
    {
        var active = fields.Where(f => !f.Archived).ToList();
        var archived = fields.Where(f => f.Archived).OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
        return SerializeFields(active) + "|" + SerializeFields(archived);
    }

    private enum CheckMode
    {
        None,
        Full,
        Shape,
    }

    private static AssetFamilyFieldImpactDto Evaluate(
        AssetFieldDefinitionDto field,
        AssetFieldDefinitionDto? previous,
        List<(Guid TenantId, JsonElement Value)> values,
        out string? message)
    {
        message = null;
        string change;
        CheckMode mode;
        var typeChanged = previous is not null && !SameType(previous, field);

        // A type change must never hide behind the archived flag: whenever the type of a key
        // changes (or a key is added over orphan values) every stored value is checked.
        if (previous is null)
        {
            change = Added;
            mode = CheckMode.Full;
        }
        else if (field.Archived)
        {
            change = previous.Archived
                ? (Same(previous, field) ? Unchanged : Updated)
                : Archived;
            mode = typeChanged ? CheckMode.Full : CheckMode.None;
        }
        else if (previous.Archived)
        {
            // Restoring a field: the shape of every stored value must fit; limits only warn.
            change = Unarchived;
            mode = typeChanged ? CheckMode.Full : CheckMode.Shape;
        }
        else if (typeChanged)
        {
            change = TypeChanged;
            mode = CheckMode.Full;
        }
        else
        {
            change = Same(previous!, field) ? Unchanged : Updated;
            mode = CheckMode.None;
        }

        var assetCount = values.Count;
        var tenantCount = values.Select(v => v.TenantId).Distinct().Count();
        var incompatible = 0;
        IReadOnlyList<string> samples = [];
        var violations = 0;

        if (mode == CheckMode.Full)
        {
            var failing = values.Where(v => !Passes(field, v.Value)).ToList();
            incompatible = failing.Count;
            samples = Samples(failing);
        }
        else if (mode == CheckMode.Shape)
        {
            var badShape = values
                .Where(v => !AssetFamilyAttributeValidator.TryCoerce(field, v.Value, out _))
                .ToList();
            incompatible = badShape.Count;
            samples = Samples(badShape);
            violations = values.Count(v => !Passes(field, v.Value)) - incompatible;
        }
        else if (!field.Archived)
        {
            violations = values.Count(v => !Passes(field, v.Value));
        }

        var blocking = incompatible > 0;
        if (blocking)
        {
            message = BlockingMessage(field, previous, typeChanged, change, incompatible, samples);
        }

        return new AssetFamilyFieldImpactDto(
            field.Key, change, assetCount, tenantCount, incompatible, samples, violations, blocking);
    }

    private static IReadOnlyList<string> Samples(List<(Guid TenantId, JsonElement Value)> failing) =>
        failing
            .Select(v => Sample(v.Value))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxSamples)
            .ToList();

    private static bool Passes(AssetFieldDefinitionDto field, JsonElement value) =>
        AssetFamilyAttributeValidator.TryCoerce(field, value, out var coerced)
        && AssetFamilyAttributeValidator.TryConstrain(field, coerced, out _, out _);

    private static string Sample(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
        return text.Length > MaxSampleLength ? text[..MaxSampleLength] : text;
    }

    private static string BlockingMessage(
        AssetFieldDefinitionDto field,
        AssetFieldDefinitionDto? previous,
        bool typeChanged,
        string change,
        int incompatible,
        IReadOnlyList<string> samples)
    {
        var action = typeChanged
            ? $"cannot change from {previous!.Type} to {field.Type}"
            : change == Unarchived
                ? $"cannot be restored as {field.Type}"
                : $"cannot be added as {field.Type}";
        var holders = incompatible == 1
            ? "1 asset holds an incompatible value"
            : $"{incompatible} assets hold incompatible values";
        var examples = string.Join(", ", samples.Select(s => $"'{s}'"));
        return $"Field '{field.Key}' {action}: {holders} (e.g. {examples}).";
    }

    private static bool SameType(AssetFieldDefinitionDto a, AssetFieldDefinitionDto b) =>
        string.Equals(a.Type?.Trim(), b.Type?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool Same(AssetFieldDefinitionDto a, AssetFieldDefinitionDto b) =>
        string.Equals(
            JsonSerializer.Serialize(a, SchemaJsonOptions),
            JsonSerializer.Serialize(b, SchemaJsonOptions),
            StringComparison.Ordinal);
}

public sealed record AssetFamilySchemaPlan(
    IReadOnlyList<AssetFieldDefinitionDto> Fields,
    IReadOnlyList<AssetFamilyFieldImpactDto> Impacts,
    IReadOnlyList<string> BlockingMessages,
    int AssetCount,
    int TenantCount)
{
    public bool CanSave => BlockingMessages.Count == 0;
}
