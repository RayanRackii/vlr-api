namespace Platform.Core.Domain.Constants;

/// <summary>
/// Static commercial/legacy module metadata. Asset Registry is a capability key in
/// <see cref="Provides"/> / <see cref="RequiredCapabilities"/>, never a tenant_modules row.
/// </summary>
public sealed record PlatformModuleDescriptor(
    string Key,
    bool IsCommercial,
    bool IsLegacy,
    IReadOnlyList<string> Provides,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<string> Aliases);

/// <summary>
/// Canonical module catalog (boring static data, not a plugin framework).
/// Persistence remains <c>core.tenant_modules</c> commercial keys plus existing <c>assets.*</c> tables.
/// </summary>
public static class PlatformModuleCatalog
{
    public const string AssetRegistryCapability = "asset-registry";

    public static IReadOnlyList<PlatformModuleDescriptor> All { get; } =
    [
        new(
            PlatformModules.Inventory,
            IsCommercial: true,
            IsLegacy: false,
            Provides: [AssetRegistryCapability],
            RequiredCapabilities: [],
            Aliases: ["inventario", "inventário"]),
        new(
            PlatformModules.Pmoc,
            IsCommercial: true,
            IsLegacy: false,
            Provides: [],
            RequiredCapabilities: [AssetRegistryCapability],
            Aliases: []),
        new(
            PlatformModules.WorkOrders,
            IsCommercial: true,
            IsLegacy: false,
            Provides: [],
            RequiredCapabilities: [AssetRegistryCapability],
            Aliases: ["workorders", "work_orders"]),
        new(
            PlatformModules.Rentals,
            IsCommercial: true,
            IsLegacy: false,
            Provides: [],
            RequiredCapabilities: [AssetRegistryCapability],
            Aliases: ["aluguel", "alugueis", "aluguéis"]),
        new(
            PlatformModules.Catalog,
            IsCommercial: true,
            IsLegacy: false,
            Provides: [],
            RequiredCapabilities: [],
            Aliases: ["catalogo", "catálogo", "orders", "pedidos"]),
        new(
            PlatformModules.Maintenance,
            IsCommercial: false,
            IsLegacy: true,
            Provides: [],
            RequiredCapabilities: [],
            Aliases: ["manutencao", "manutenção"]),
    ];

    private static readonly Dictionary<string, string> AliasToKey = BuildAliasIndex();

    private static readonly HashSet<string> LegacyKeys = All
        .Where(m => m.IsLegacy)
        .Select(m => m.Key)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<PlatformModuleDescriptor> Commercial { get; } =
        All.Where(m => m.IsCommercial && !m.IsLegacy).ToArray();

    public static bool TryNormalize(string? moduleName, out string canonical)
    {
        canonical = string.Empty;

        if (string.IsNullOrWhiteSpace(moduleName))
        {
            return false;
        }

        if (!AliasToKey.TryGetValue(moduleName.Trim(), out var mapped))
        {
            return false;
        }

        canonical = mapped;
        return true;
    }

    public static bool IsLegacy(string? moduleName)
    {
        if (!TryNormalize(moduleName, out var canonical))
        {
            return false;
        }

        return LegacyKeys.Contains(canonical);
    }

    /// <summary>
    /// Normalizes requested commercial keys. Never inserts inventory because dependents are present.
    /// Rejects unknown keys and new activation of legacy modules. Existing legacy keys are omitted
    /// from the result; callers must not delete them (<see cref="ShouldRemoveStoredModule"/>).
    /// </summary>
    public static IReadOnlyList<string> NormalizeEntitlements(
        IReadOnlyList<string>? requested,
        IReadOnlyCollection<string>? existingActiveKeys = null)
    {
        if (requested is null || requested.Count == 0)
        {
            return [];
        }

        var existingActive = existingActiveKeys is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(existingActiveKeys, StringComparer.OrdinalIgnoreCase);

        var commercial = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var module in requested)
        {
            if (!TryNormalize(module, out var canonical))
            {
                throw new ArgumentException($"Unknown module '{module}'.");
            }

            if (IsLegacy(canonical))
            {
                if (!existingActive.Contains(canonical))
                {
                    throw new ArgumentException($"Cannot activate legacy module {canonical}.");
                }

                continue;
            }

            commercial.Add(canonical);
        }

        return commercial.OrderBy(m => m, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Legacy rows stay even when omitted from the desired commercial set (Wave 1).
    /// </summary>
    public static bool ShouldRemoveStoredModule(
        string storedModuleName,
        IReadOnlyCollection<string> desiredCommercialKeys)
    {
        if (IsLegacy(storedModuleName))
        {
            return false;
        }

        foreach (var key in desiredCommercialKeys)
        {
            if (string.Equals(key, storedModuleName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, string> BuildAliasIndex()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var module in All)
        {
            map[module.Key] = module.Key;

            foreach (var alias in module.Aliases)
            {
                map[alias] = module.Key;
            }
        }

        return map;
    }
}
