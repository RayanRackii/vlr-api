using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Platform.Core.Infrastructure.Persistence;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20261110160000_AddCoreNotificationResendPermission")]
    public class AddCoreNotificationResendPermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                INSERT INTO core.permissions (id, key, name, description, module_key, created_at)
                VALUES (
                    gen_random_uuid(),
                    'core.notifications.resend',
                    'Resend notifications',
                    'Resend a failed notification delivery.',
                    NULL,
                    now())
                ON CONFLICT (key) DO NOTHING;

                INSERT INTO core.role_permissions (role_id, permission_id, granted_at)
                SELECT existing.role_id, added.id, now()
                FROM core.role_permissions existing
                JOIN core.permissions source
                    ON source.id = existing.permission_id
                   AND source.key = 'catalog.notifications.resend'
                JOIN core.permissions added
                    ON added.key = 'core.notifications.resend'
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
                    SELECT id FROM core.permissions WHERE key = 'core.notifications.resend');

                DELETE FROM core.permissions
                WHERE key = 'core.notifications.resend';
                """);
        }
    }
}
