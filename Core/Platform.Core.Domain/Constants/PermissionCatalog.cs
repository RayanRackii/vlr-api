namespace Platform.Core.Domain.Constants;

public sealed record PermissionDefinition(
    string Key,
    string Name,
    string Description,
    string? ModuleKey,
    string Resource);

/// <summary>
/// System-defined permission catalog (43 keys). Tenants cannot create keys.
/// </summary>
public static class PermissionCatalog
{
    public static readonly PermissionDefinition[] All =
    [
        Define(Permissions.Core.DashboardRead, "Read dashboard", "View tenant dashboard metrics."),
        Define(Permissions.Core.UsersRead, "Read users", "List tenant users."),
        Define(Permissions.Core.UsersInvite, "Invite users", "Invite users to the tenant."),
        Define(Permissions.Core.UsersAssignRoles, "Assign user roles", "Change roles assigned to a user."),
        Define(Permissions.Core.RolesRead, "Read roles", "List roles and the permission catalog."),
        Define(Permissions.Core.RolesManage, "Manage roles", "Create, update, and delete custom roles."),
        Define(Permissions.Core.UnitsRead, "Read units", "List tenant units."),
        Define(Permissions.Core.RegistrationFieldsRead, "Read registration fields", "List B2C registration fields."),
        Define(Permissions.Core.RegistrationFieldsWrite, "Write registration fields", "Create and update B2C registration fields."),
        Define(Permissions.Core.ModuleMenuRead, "Read module menu", "List B2C module menu items."),
        Define(Permissions.Core.ModuleMenuWrite, "Write module menu", "Create and update B2C module menu items."),
        Define(Permissions.Inventory.AssetsRead, "Read assets", "List and view inventory assets."),
        Define(Permissions.Inventory.AssetsWrite, "Write assets", "Create, update, and delete inventory assets."),
        Define(Permissions.Inventory.CategoriesRead, "Read asset categories", "List and view asset categories."),
        Define(Permissions.Inventory.CategoriesWrite, "Write asset categories", "Create, update, and delete asset categories."),
        Define(Permissions.Inventory.FamiliesRead, "Read asset families", "List asset families."),
        Define(Permissions.Pmoc.PlansRead, "Read PMOC plans", "List and view maintenance plans."),
        Define(Permissions.Pmoc.PlansWrite, "Write PMOC plans", "Create, update, and delete maintenance plans."),
        Define(Permissions.Pmoc.TemplatesRead, "Read PMOC templates", "List global maintenance templates."),
        Define(Permissions.Os.WorkOrdersRead, "Read work orders", "List and view work orders."),
        Define(Permissions.Os.WorkOrdersCreate, "Create work orders", "Create work orders."),
        Define(Permissions.Os.WorkOrdersExecute, "Execute work orders", "Update work-order tasks and status."),
        Define(Permissions.Os.WorkOrdersAssign, "Assign work orders", "Choose eligible work-order assignees."),
        Define(Permissions.Rentals.ReservationsRead, "Read reservations", "List tenant reservations."),
        Define(Permissions.Rentals.ReservationsConfirm, "Confirm reservations", "Confirm pending reservations."),
        Define(Permissions.Rentals.ReservationsCancel, "Cancel reservations", "Cancel reservations."),
        Define(Permissions.Rentals.ScheduleRead, "Read schedule", "View schedule templates and days."),
        Define(Permissions.Rentals.ScheduleWrite, "Write schedule", "Create and update schedule templates and slots."),
        Define(Permissions.Rentals.AssetsRead, "Read rental assets", "List rentable assets."),
        Define(Permissions.Rentals.AssetsWrite, "Write rental assets", "Update rental schedule policy."),
        Define(Permissions.Rentals.PricingRead, "Read rental pricing", "List rental pricing windows."),
        Define(Permissions.Rentals.PricingWrite, "Write rental pricing", "Create, update, and delete rental pricing."),
        Define(Permissions.Rentals.PricingBulkWrite, "Bulk write rental pricing", "Apply rental pricing in bulk."),
        Define(Permissions.Rentals.LayoutsRead, "Read rental layouts", "List and view rental layouts."),
        Define(Permissions.Rentals.LayoutsWrite, "Write rental layouts", "Create, update, and delete rental layouts."),
        Define(Permissions.Rentals.OccupancyKindsRead, "Read occupancy kinds", "List occupancy kinds."),
        Define(Permissions.Rentals.OccupancyKindsWrite, "Write occupancy kinds", "Create and update occupancy kinds."),
        Define(Permissions.Catalog.ProductsRead, "Read catalog products", "List and view catalog products."),
        Define(Permissions.Catalog.ProductsManage, "Manage catalog products", "Create, update, and deactivate catalog products and files."),
        Define(Permissions.Catalog.OrdersRead, "Read catalog orders", "List and view catalog orders."),
        Define(Permissions.Catalog.OrdersManage, "Manage catalog orders", "Approve, reject, fulfill, and cancel catalog orders."),
        Define(Permissions.Catalog.NotificationsRead, "Read catalog notifications", "List catalog notification deliveries and channel config."),
        Define(Permissions.Catalog.NotificationsResend, "Resend catalog notifications", "Resend failed deliveries and update catalog channel config."),
    ];

    public static readonly IReadOnlySet<string> AllKeys =
        All.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> DefaultUserKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        Permissions.Core.DashboardRead,
        Permissions.Core.UnitsRead,
        Permissions.Inventory.AssetsRead,
        Permissions.Inventory.CategoriesRead,
        Permissions.Inventory.FamiliesRead,
        Permissions.Pmoc.PlansRead,
        Permissions.Pmoc.TemplatesRead,
        Permissions.Os.WorkOrdersRead,
        Permissions.Os.WorkOrdersExecute,
        Permissions.Rentals.ReservationsRead,
        Permissions.Rentals.ScheduleRead,
        Permissions.Rentals.AssetsRead,
        Permissions.Rentals.PricingRead,
        Permissions.Rentals.LayoutsRead,
        Permissions.Rentals.OccupancyKindsRead,
    };

    public static readonly IReadOnlySet<string> TechnicianLegacyKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        Permissions.Os.WorkOrdersRead,
        Permissions.Os.WorkOrdersExecute,
        Permissions.Inventory.AssetsRead,
    };

    public static bool IsCoreModule(string? moduleKey) =>
        string.IsNullOrWhiteSpace(moduleKey)
        || moduleKey.Equals("core", StringComparison.OrdinalIgnoreCase);

    public static string ResourceOf(string key)
    {
        var parts = key.Split('.', 3, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1] : key;
    }

    public static string? ModuleKeyOf(string key)
    {
        var prefix = key.Split('.', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        return prefix.Equals("core", StringComparison.OrdinalIgnoreCase) ? null : prefix;
    }

    private static PermissionDefinition Define(string key, string name, string description)
    {
        var moduleKey = ModuleKeyOf(key);
        return new PermissionDefinition(key, name, description, moduleKey, ResourceOf(key));
    }
}
