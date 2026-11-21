using System.Text.Json;
using Platform.Core.Domain.Common;
using Platform.Core.Domain.Constants;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// One sellable plan of a package (<c>billing.plan_catalog</c>). Platform-wide data seeded by migration:
/// prices, default modules and limits are rows, never code. Not tenant scoped.
/// </summary>
public class PlanCatalog : Entity
{
    public const int KeyMaxLength = 32;

    public const int NameMaxLength = 100;

    public string PackageKey { get; set; } = string.Empty;

    public string PlanKey { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int MonthlyPriceCents { get; set; }

    public int AnnualPriceCents { get; set; }

    public string Currency { get; set; } = "BRL";

    /// <summary>jsonb array of module keys applied at signup (trial creation) only.</summary>
    public string ModulesJson { get; set; } = "[]";

    /// <summary>jsonb object, see <see cref="PlanLimitKeys"/>; null or missing key = unlimited.</summary>
    public string LimitsJson { get; set; } = "{}";

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    public IReadOnlyList<string> GetModules() => ParseModules(ModulesJson);

    public PlanLimitValues GetLimits() => PlanLimitValues.Parse(LimitsJson);

    public static IReadOnlyList<string> ParseModules(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return document.RootElement
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToList();
    }
}

/// <summary>Parsed <c>limits</c> of a plan. A null value means unlimited.</summary>
public sealed record PlanLimitValues(
    int? Assets,
    int? RentableResources,
    int? Users,
    int? Units,
    int? WhatsappMessagesMonth)
{
    public static readonly PlanLimitValues Unlimited = new(null, null, null, null, null);

    public static PlanLimitValues Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Unlimited;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Unlimited;
        }

        return new PlanLimitValues(
            Read(root, PlanLimitKeys.Assets),
            Read(root, PlanLimitKeys.RentableResources),
            Read(root, PlanLimitKeys.Users),
            Read(root, PlanLimitKeys.Units),
            Read(root, PlanLimitKeys.WhatsappMessagesMonth));
    }

    private static int? Read(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
                ? number
                : null;
}
