namespace Platform.Core.Domain.Constants;

/// <summary>
/// Canonical module keys stored in core.tenant_modules.module_name.
/// </summary>
public static class PlatformModules
{
    public const string Inventory = "inventory";

    public const string Maintenance = "maintenance";

    public const string Pmoc = "pmoc";

    public const string WorkOrders = "os";

    public const string Rentals = "rentals";

    public const string Catalog = "catalog";

    /// <summary>
    /// Maps API / UI module labels (e.g. "Rentals", "PMOC") to canonical keys.
    /// </summary>
    public static bool TryNormalize(string? moduleName, out string canonical) =>
        PlatformModuleCatalog.TryNormalize(moduleName, out canonical);
}
