using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Insert-only, idempotent grants of the resource-planning permissions to the system roles of
    /// tenants that already exist. New tenants get them from <c>TenantAccessBootstrapper</c>; a
    /// tenant created before slice API-2 never ran it again, so its Technician role would miss the
    /// agenda and execute keys (and its Admin roles the catalog rows). The grants mirror the
    /// bootstrapper: Admin and SuperAdmin receive every key of the module, Technician receives
    /// <c>agenda.read</c> and <c>allocations.execute</c>. No data change, no model change.
    /// </summary>
    public partial class GrantResourcePlanningPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO core.permissions (id, key, name, description, module_key, created_at)
                VALUES
                    (gen_random_uuid(), 'resource-planning.agenda.read', 'Read resource planning', 'List plannable assets, read their planning profile, agenda and allocations.', 'resource-planning', now()),
                    (gen_random_uuid(), 'resource-planning.allocations.manage', 'Manage allocations', 'Create, edit, and cancel any resource allocation.', 'resource-planning', now()),
                    (gen_random_uuid(), 'resource-planning.allocations.execute', 'Execute allocations', 'Create allocations and start, finish, and release them.', 'resource-planning', now()),
                    (gen_random_uuid(), 'resource-planning.availability.write', 'Write asset availability', 'Edit an asset''s planning profile and working hours.', 'resource-planning', now())
                ON CONFLICT (key) DO NOTHING;

                INSERT INTO core.role_permissions (role_id, permission_id, granted_at)
                SELECT r.id, p.id, now()
                FROM core.roles r CROSS JOIN core.permissions p
                WHERE r.is_system_role
                  AND lower(r.name) IN ('admin', 'superadmin')
                  AND p.key IN (
                    'resource-planning.agenda.read',
                    'resource-planning.allocations.manage',
                    'resource-planning.allocations.execute',
                    'resource-planning.availability.write')
                ON CONFLICT DO NOTHING;

                INSERT INTO core.role_permissions (role_id, permission_id, granted_at)
                SELECT r.id, p.id, now()
                FROM core.roles r CROSS JOIN core.permissions p
                WHERE lower(r.name) = 'technician'
                  AND p.key IN (
                    'resource-planning.agenda.read',
                    'resource-planning.allocations.execute')
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Guarded no-op. The grants cannot be told apart from the same grants an admin or the
            // bootstrapper made on purpose, and revoking them would silently lock technicians out.
            // The permission rows stay too: they belong to the catalog, not to this migration.
        }
    }
}
