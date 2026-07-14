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

    /// <summary>
    /// Maps API / UI module labels (e.g. "Rentals", "PMOC") to canonical keys.
    /// </summary>
    public static bool TryNormalize(string? moduleName, out string canonical)
    {
        canonical = string.Empty;

        if (string.IsNullOrWhiteSpace(moduleName))
        {
            return false;
        }

        switch (moduleName.Trim().ToLowerInvariant())
        {
            case "inventory":
            case "inventario":
            case "inventário":
                canonical = Inventory;
                return true;
            case "pmoc":
                canonical = Pmoc;
                return true;
            case "os":
            case "workorders":
            case "work_orders":
                canonical = WorkOrders;
                return true;
            case "rentals":
            case "aluguel":
            case "alugueis":
            case "aluguéis":
                canonical = Rentals;
                return true;
            case "maintenance":
            case "manutencao":
            case "manutenção":
                canonical = Maintenance;
                return true;
            default:
                return false;
        }
    }
}
