namespace Platform.Core.Domain.Constants;

/// <summary>
/// Canonical tenant RBAC permission keys. Nested classes match the catalog prefix.
/// </summary>
public static class Permissions
{
    public static class Core
    {
        public const string DashboardRead = "core.dashboard.read";
        public const string UsersRead = "core.users.read";
        public const string UsersInvite = "core.users.invite";
        public const string UsersAssignRoles = "core.users.assign_roles";
        public const string RolesRead = "core.roles.read";
        public const string RolesManage = "core.roles.manage";
        public const string UnitsRead = "core.units.read";
        public const string RegistrationFieldsRead = "core.registration_fields.read";
        public const string RegistrationFieldsWrite = "core.registration_fields.write";
        public const string ModuleMenuRead = "core.module_menu.read";
        public const string ModuleMenuWrite = "core.module_menu.write";
    }

    public static class Inventory
    {
        public const string AssetsRead = "inventory.assets.read";
        public const string AssetsWrite = "inventory.assets.write";
        public const string CategoriesRead = "inventory.categories.read";
        public const string CategoriesWrite = "inventory.categories.write";
        public const string FamiliesRead = "inventory.families.read";
    }

    public static class Pmoc
    {
        public const string PlansRead = "pmoc.plans.read";
        public const string PlansWrite = "pmoc.plans.write";
        public const string TemplatesRead = "pmoc.templates.read";
    }

    public static class Os
    {
        public const string WorkOrdersRead = "os.work_orders.read";
        public const string WorkOrdersCreate = "os.work_orders.create";
        public const string WorkOrdersExecute = "os.work_orders.execute";
        public const string WorkOrdersAssign = "os.work_orders.assign";
    }

    public static class Rentals
    {
        public const string ReservationsRead = "rentals.reservations.read";
        public const string ReservationsConfirm = "rentals.reservations.confirm";
        public const string ReservationsCancel = "rentals.reservations.cancel";
        public const string ScheduleRead = "rentals.schedule.read";
        public const string ScheduleWrite = "rentals.schedule.write";
        public const string AssetsRead = "rentals.assets.read";
        public const string AssetsWrite = "rentals.assets.write";
        public const string PricingRead = "rentals.pricing.read";
        public const string PricingWrite = "rentals.pricing.write";
        public const string PricingBulkWrite = "rentals.pricing.bulk_write";
        public const string LayoutsRead = "rentals.layouts.read";
        public const string LayoutsWrite = "rentals.layouts.write";
        public const string OccupancyKindsRead = "rentals.occupancy_kinds.read";
        public const string OccupancyKindsWrite = "rentals.occupancy_kinds.write";
    }
}
