namespace Platform.Core.Domain.Constants;

/// <summary>
/// Canonical example <c>AssetCategory</c> names keyed by asset family.
/// <see cref="AssetFamilyKeys.Generic"/> has no entry — do not invent a tipo.
/// </summary>
public static class AssetCategoryExampleSeeds
{
    public const string PmocRequiresProvisioningFamilyMessage =
        "PMOC requires at least one asset family with available resource types.";

    public static IReadOnlyDictionary<string, string> ByFamilyKey { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [AssetFamilyKeys.Spaces] = "Quadra",
            [AssetFamilyKeys.Electrical] = "Quadro elétrico",
            [AssetFamilyKeys.Goods] = "Caçamba",
        };

    public static bool CanProvisionExampleCategory(string? familyKey) =>
        !string.IsNullOrWhiteSpace(familyKey)
        && ByFamilyKey.ContainsKey(familyKey.Trim());

    public static bool HasPmocProvisioningFamily(IEnumerable<string> familyKeys) =>
        familyKeys.Any(CanProvisionExampleCategory);
}
