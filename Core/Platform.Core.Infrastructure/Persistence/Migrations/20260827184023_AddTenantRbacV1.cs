using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantRbacV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_invite_roles",
                schema: "core",
                columns: table => new
                {
                    invite_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_invite_roles", x => new { x.invite_id, x.role_id });
                    table.ForeignKey(
                        name: "fk_user_invite_roles_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "core",
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_invite_roles_user_invites_invite_id",
                        column: x => x.invite_id,
                        principalSchema: "core",
                        principalTable: "user_invites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_invite_roles_role_id",
                schema: "core",
                table: "user_invite_roles",
                column: "role_id");

            migrationBuilder.Sql(
                """
                INSERT INTO core.permissions (id, key, name, description, module_key, created_at)
                VALUES
                    (gen_random_uuid(), 'core.dashboard.read', 'Read dashboard', 'View tenant dashboard metrics.', NULL, now()),
                    (gen_random_uuid(), 'core.users.read', 'Read users', 'List tenant users.', NULL, now()),
                    (gen_random_uuid(), 'core.users.invite', 'Invite users', 'Invite users to the tenant.', NULL, now()),
                    (gen_random_uuid(), 'core.users.assign_roles', 'Assign user roles', 'Change roles assigned to a user.', NULL, now()),
                    (gen_random_uuid(), 'core.roles.read', 'Read roles', 'List roles and the permission catalog.', NULL, now()),
                    (gen_random_uuid(), 'core.roles.manage', 'Manage roles', 'Create, update, and delete custom roles.', NULL, now()),
                    (gen_random_uuid(), 'core.units.read', 'Read units', 'List tenant units.', NULL, now()),
                    (gen_random_uuid(), 'core.registration_fields.read', 'Read registration fields', 'List B2C registration fields.', NULL, now()),
                    (gen_random_uuid(), 'core.registration_fields.write', 'Write registration fields', 'Create and update B2C registration fields.', NULL, now()),
                    (gen_random_uuid(), 'core.module_menu.read', 'Read module menu', 'List B2C module menu items.', NULL, now()),
                    (gen_random_uuid(), 'core.module_menu.write', 'Write module menu', 'Create and update B2C module menu items.', NULL, now()),
                    (gen_random_uuid(), 'inventory.assets.read', 'Read assets', 'List and view inventory assets.', 'inventory', now()),
                    (gen_random_uuid(), 'inventory.assets.write', 'Write assets', 'Create, update, and delete inventory assets.', 'inventory', now()),
                    (gen_random_uuid(), 'inventory.categories.read', 'Read asset categories', 'List and view asset categories.', 'inventory', now()),
                    (gen_random_uuid(), 'inventory.categories.write', 'Write asset categories', 'Create, update, and delete asset categories.', 'inventory', now()),
                    (gen_random_uuid(), 'inventory.families.read', 'Read asset families', 'List asset families.', 'inventory', now()),
                    (gen_random_uuid(), 'pmoc.plans.read', 'Read PMOC plans', 'List and view maintenance plans.', 'pmoc', now()),
                    (gen_random_uuid(), 'pmoc.plans.write', 'Write PMOC plans', 'Create, update, and delete maintenance plans.', 'pmoc', now()),
                    (gen_random_uuid(), 'pmoc.templates.read', 'Read PMOC templates', 'List global maintenance templates.', 'pmoc', now()),
                    (gen_random_uuid(), 'os.work_orders.read', 'Read work orders', 'List and view work orders.', 'os', now()),
                    (gen_random_uuid(), 'os.work_orders.create', 'Create work orders', 'Create work orders.', 'os', now()),
                    (gen_random_uuid(), 'os.work_orders.execute', 'Execute work orders', 'Update work-order tasks and status.', 'os', now()),
                    (gen_random_uuid(), 'os.work_orders.assign', 'Assign work orders', 'Choose eligible work-order assignees.', 'os', now()),
                    (gen_random_uuid(), 'rentals.reservations.read', 'Read reservations', 'List tenant reservations.', 'rentals', now()),
                    (gen_random_uuid(), 'rentals.reservations.confirm', 'Confirm reservations', 'Confirm pending reservations.', 'rentals', now()),
                    (gen_random_uuid(), 'rentals.reservations.cancel', 'Cancel reservations', 'Cancel reservations.', 'rentals', now()),
                    (gen_random_uuid(), 'rentals.schedule.read', 'Read schedule', 'View schedule templates and days.', 'rentals', now()),
                    (gen_random_uuid(), 'rentals.schedule.write', 'Write schedule', 'Create and update schedule templates and slots.', 'rentals', now()),
                    (gen_random_uuid(), 'rentals.assets.read', 'Read rental assets', 'List rentable assets.', 'rentals', now()),
                    (gen_random_uuid(), 'rentals.assets.write', 'Write rental assets', 'Update rental schedule policy.', 'rentals', now()),
                    (gen_random_uuid(), 'rentals.pricing.read', 'Read rental pricing', 'List rental pricing windows.', 'rentals', now()),
                    (gen_random_uuid(), 'rentals.pricing.write', 'Write rental pricing', 'Create, update, and delete rental pricing.', 'rentals', now()),
                    (gen_random_uuid(), 'rentals.pricing.bulk_write', 'Bulk write rental pricing', 'Apply rental pricing in bulk.', 'rentals', now()),
                    (gen_random_uuid(), 'rentals.layouts.read', 'Read rental layouts', 'List and view rental layouts.', 'rentals', now()),
                    (gen_random_uuid(), 'rentals.layouts.write', 'Write rental layouts', 'Create, update, and delete rental layouts.', 'rentals', now()),
                    (gen_random_uuid(), 'rentals.occupancy_kinds.read', 'Read occupancy kinds', 'List occupancy kinds.', 'rentals', now()),
                    (gen_random_uuid(), 'rentals.occupancy_kinds.write', 'Write occupancy kinds', 'Create and update occupancy kinds.', 'rentals', now())
                ON CONFLICT (key) DO NOTHING;

                INSERT INTO core.roles (id, tenant_id, name, description, is_system_role, created_at)
                SELECT gen_random_uuid(), t.id, 'Admin', 'Admin (system)', true, now()
                FROM core.tenants t
                WHERE NOT EXISTS (
                    SELECT 1 FROM core.roles r
                    WHERE r.tenant_id = t.id AND lower(r.name) = 'admin');

                INSERT INTO core.roles (id, tenant_id, name, description, is_system_role, created_at)
                SELECT gen_random_uuid(), t.id, 'User', 'User (system)', true, now()
                FROM core.tenants t
                WHERE NOT EXISTS (
                    SELECT 1 FROM core.roles r
                    WHERE r.tenant_id = t.id AND lower(r.name) = 'user');

                INSERT INTO core.role_permissions (role_id, permission_id, granted_at)
                SELECT r.id, p.id, now()
                FROM core.roles r
                CROSS JOIN core.permissions p
                WHERE lower(r.name) IN ('admin', 'superadmin')
                ON CONFLICT DO NOTHING;

                INSERT INTO core.role_permissions (role_id, permission_id, granted_at)
                SELECT r.id, p.id, now()
                FROM core.roles r
                INNER JOIN core.permissions p ON p.key IN (
                    'core.dashboard.read',
                    'core.units.read',
                    'inventory.assets.read',
                    'inventory.categories.read',
                    'inventory.families.read',
                    'pmoc.plans.read',
                    'pmoc.templates.read',
                    'os.work_orders.read',
                    'os.work_orders.execute',
                    'rentals.reservations.read',
                    'rentals.schedule.read',
                    'rentals.assets.read',
                    'rentals.pricing.read',
                    'rentals.layouts.read',
                    'rentals.occupancy_kinds.read')
                WHERE lower(r.name) = 'user'
                ON CONFLICT DO NOTHING;

                INSERT INTO core.role_permissions (role_id, permission_id, granted_at)
                SELECT r.id, p.id, now()
                FROM core.roles r
                INNER JOIN core.permissions p ON p.key IN (
                    'os.work_orders.read',
                    'os.work_orders.execute',
                    'inventory.assets.read')
                WHERE lower(r.name) = 'technician'
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_invite_roles",
                schema: "core");
        }
    }
}
