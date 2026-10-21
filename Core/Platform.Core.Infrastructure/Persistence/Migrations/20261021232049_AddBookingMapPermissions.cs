using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingMapPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO core.permissions (id, key, name, description, module_key, created_at)
                VALUES
                    (gen_random_uuid(), 'rentals.segments.read', 'Read resource segments', 'View tenant resource discovery segments.', 'rentals', now()),
                    (gen_random_uuid(), 'rentals.segments.write', 'Write resource segments', 'Replace tenant resource discovery segments.', 'rentals', now()),
                    (gen_random_uuid(), 'rentals.map_settings.read', 'Read booking map settings', 'View tenant booking map presentation settings.', 'rentals', now()),
                    (gen_random_uuid(), 'rentals.map_settings.write', 'Write booking map settings', 'Update tenant booking map presentation settings.', 'rentals', now())
                ON CONFLICT (key) DO NOTHING;

                INSERT INTO core.role_permissions (role_id, permission_id, granted_at)
                SELECT r.id, p.id, now()
                FROM core.roles r CROSS JOIN core.permissions p
                WHERE lower(r.name) IN ('admin', 'superadmin') AND p.key IN (
                    'rentals.segments.read', 'rentals.segments.write',
                    'rentals.map_settings.read', 'rentals.map_settings.write')
                ON CONFLICT DO NOTHING;
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM core.role_permissions WHERE permission_id IN (
                    SELECT id FROM core.permissions WHERE key IN (
                        'rentals.segments.read', 'rentals.segments.write',
                        'rentals.map_settings.read', 'rentals.map_settings.write'));
                DELETE FROM core.permissions WHERE key IN (
                    'rentals.segments.read', 'rentals.segments.write',
                    'rentals.map_settings.read', 'rentals.map_settings.write');
                """);

        }
    }
}
