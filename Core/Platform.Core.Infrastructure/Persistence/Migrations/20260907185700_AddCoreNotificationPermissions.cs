using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCoreNotificationPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                INSERT INTO core.permissions (id, key, name, description, module_key, created_at)
                VALUES
                    (gen_random_uuid(), 'core.notifications.read', 'Read notification settings', 'List tenant notification channel configuration.', NULL, now()),
                    (gen_random_uuid(), 'core.notifications.write', 'Write notification settings', 'Update tenant notification channel configuration.', NULL, now())
                ON CONFLICT (key) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM core.role_permissions
                WHERE permission_id IN (
                    SELECT id FROM core.permissions
                    WHERE key IN ('core.notifications.read', 'core.notifications.write'));

                DELETE FROM core.permissions
                WHERE key IN ('core.notifications.read', 'core.notifications.write');
                """);
        }
    }
}
