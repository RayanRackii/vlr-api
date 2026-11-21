using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;

namespace Platform.Api.Services.Tenancy;

/// <summary>
/// Row filter applied to the planning allocations a module purge takes along. Planning
/// allocations are shared by rentals, catalog and os: a module purge only owns the rows it
/// created, never the whole table (the <c>resource-planning</c> purge owns all of it).
/// </summary>
internal enum PlanningRowFilter
{
    None = 0,

    /// <summary><c>Source = CatalogOrder</c>.</summary>
    CatalogOrderSource,

    /// <summary>Rows bridged to a rental reservation item (<c>ReservationItemId</c> set).</summary>
    ReservationBridge,

    /// <summary><c>Source = WorkOrder</c>.</summary>
    WorkOrderSource,
}

/// <summary>One entity of a module purge plan; <see cref="RowFilter"/> is set only on borrowed planning rows.</summary>
internal sealed record ModuleScopeEntry(IEntityType Entity, PlanningRowFilter RowFilter = PlanningRowFilter.None);

/// <summary>
/// Which tenant data belongs to which module, derived from the table schema in the EF model
/// (spec 2.1, docs/plans/active/2026-11-20-module-data-purge.md). <c>assets</c> (shared asset registry) and
/// <c>core</c> (platform) are never purgeable. A new schema must be classified here explicitly.
/// </summary>
internal static class ModuleDataScope
{
    internal const string UnknownModuleCode = "module.unknown";
    internal const string NotPurgeableCode = "module.not_purgeable";

    /// <summary>Schema to owning module key. <c>null</c> = shared/platform schema, never purgeable.</summary>
    private static readonly IReadOnlyDictionary<string, string?> SchemaOwners =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["rentals"] = PlatformModules.Rentals,
            ["catalog"] = PlatformModules.Catalog,
            ["os"] = PlatformModules.WorkOrders,
            ["pmoc"] = PlatformModules.Pmoc,
            ["planning"] = PlatformModules.ResourcePlanning,
            ["assets"] = null,
            ["core"] = null,
        };

    internal static IReadOnlyCollection<string> PurgeableModules { get; } = SchemaOwners.Values
        .OfType<string>()
        .Order(StringComparer.Ordinal)
        .ToArray();

    /// <summary>True when the schema is listed in the spec table (purgeable or deliberately not).</summary>
    internal static bool IsClassified(string? schema) =>
        schema is not null && SchemaOwners.ContainsKey(schema);

    /// <summary>The module that owns the schema, or null for shared/platform/unknown schemas.</summary>
    internal static string? ModuleOfSchema(string? schema) =>
        schema is not null && SchemaOwners.TryGetValue(schema, out var module) ? module : null;

    internal static bool IsPurgeable(string? module) =>
        module is not null && PurgeableModules.Contains(module, StringComparer.Ordinal);

    internal static string SchemaOf(IEntityType entity, IModel model) =>
        entity.GetSchema() ?? model.GetDefaultSchema() ?? "core";

    internal static string SchemaFor(string module) =>
        SchemaOwners.FirstOrDefault(pair => string.Equals(pair.Value, module, StringComparison.Ordinal)).Key
        ?? throw new ArgumentException($"Module '{module}' is not purgeable.", nameof(module));

    /// <summary>
    /// Resolves an API module key. Unknown keys give <see cref="UnknownModuleCode"/>; known modules
    /// without purgeable data (inventory, the legacy maintenance key) and the <c>assets</c>/<c>core</c>
    /// schema names give <see cref="NotPurgeableCode"/>.
    /// </summary>
    internal static (string? Module, string? ErrorCode) Resolve(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return (null, UnknownModuleCode);

        var trimmed = key.Trim();
        if (PlatformModules.TryNormalize(trimmed, out var canonical))
            return IsPurgeable(canonical) ? (canonical, null) : (canonical, NotPurgeableCode);

        return IsClassified(trimmed.ToLowerInvariant())
            ? (trimmed.ToLowerInvariant(), NotPurgeableCode)
            : (null, UnknownModuleCode);
    }

    /// <summary>
    /// Entities with <c>TenantId</c> in the module's schema plus their FK dependents inside the same
    /// schema, leaf-first (the order shared with <see cref="TenantPurger"/>).
    /// </summary>
    internal static IReadOnlyList<IEntityType> EntitiesFor(IModel model, string module) =>
        TenantDeletionOrder.Order(CoverSchema(model, module, borrowPlanning: false));

    /// <summary>
    /// <see cref="EntitiesFor"/> plus the planning allocations the module owns (amendment 7.1):
    /// catalog takes <c>Source = CatalogOrder</c> rows, rentals the rows bridged to a reservation
    /// item, os the <c>Source = WorkOrder</c> rows. The <c>resource-planning</c> module owns the
    /// whole planning schema, so nothing is borrowed. Leaf-first, so catalog allocations come
    /// before the planning rows they point at, and those before the order items they point at.
    /// </summary>
    internal static IReadOnlyList<ModuleScopeEntry> PlanFor(IModel model, string module)
    {
        var filter = PlanningFilterFor(module);
        var order = TenantDeletionOrder.Order(
            CoverSchema(model, module, borrowPlanning: filter != PlanningRowFilter.None));
        return order
            .Select(entity => new ModuleScopeEntry(
                entity,
                entity.ClrType == typeof(AssetAllocation) ? filter : PlanningRowFilter.None))
            .ToArray();
    }

    internal static PlanningRowFilter PlanningFilterFor(string module) => module switch
    {
        PlatformModules.Catalog => PlanningRowFilter.CatalogOrderSource,
        PlatformModules.Rentals => PlanningRowFilter.ReservationBridge,
        PlatformModules.WorkOrders => PlanningRowFilter.WorkOrderSource,
        _ => PlanningRowFilter.None,
    };

    private static HashSet<IEntityType> CoverSchema(IModel model, string module, bool borrowPlanning)
    {
        var schema = SchemaFor(module);
        var pool = model.GetEntityTypes()
            .Where(entity => string.Equals(SchemaOf(entity, model), schema, StringComparison.Ordinal))
            .ToList();
        if (borrowPlanning)
        {
            var allocation = model.FindEntityType(typeof(AssetAllocation))
                ?? throw new InvalidOperationException("AssetAllocation is not part of the model.");
            if (!pool.Contains(allocation))
                pool.Add(allocation);
        }

        return TenantDeletionOrder.Cover(pool, entity => entity.FindProperty("TenantId") is not null);
    }
}
