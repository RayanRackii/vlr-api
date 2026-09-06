using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRentalsReservationsCompletePermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                INSERT INTO core.permissions (id, key, name, description, module_key, created_at)
                VALUES
                    (gen_random_uuid(), 'rentals.reservations.complete', 'Complete reservations', 'Mark confirmed reservations as completed.', 'rentals', now())
                ON CONFLICT (key) DO NOTHING;

                INSERT INTO core.role_permissions (role_id, permission_id, granted_at)
                SELECT r.id, p.id, now()
                FROM core.roles r
                CROSS JOIN core.permissions p
                WHERE lower(r.name) IN ('admin', 'superadmin')
                  AND p.key = 'rentals.reservations.complete'
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM core.role_permissions
                WHERE permission_id IN (
                    SELECT id FROM core.permissions WHERE key = 'rentals.reservations.complete');

                DELETE FROM core.permissions WHERE key = 'rentals.reservations.complete';
                """);
        }
    }
}
