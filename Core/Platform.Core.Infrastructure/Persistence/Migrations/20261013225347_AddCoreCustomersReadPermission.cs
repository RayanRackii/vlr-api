using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    public partial class AddCoreCustomersReadPermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                INSERT INTO core.permissions (id, key, name, description, module_key, created_at)
                VALUES
                    (gen_random_uuid(), 'core.customers.read', 'Read portal customers', 'List portal customers of the current tenant.', NULL, now())
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
                    SELECT id FROM core.permissions WHERE key = 'core.customers.read');

                DELETE FROM core.permissions WHERE key = 'core.customers.read';
                """);
        }
    }
}
