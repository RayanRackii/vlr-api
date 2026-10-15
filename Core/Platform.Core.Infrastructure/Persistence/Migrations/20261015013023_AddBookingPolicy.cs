using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "booking_policies",
                schema: "rentals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    cancellation_policy_text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    max_active_reservations_per_customer_per_day = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_booking_policies", x => x.id);
                    table.CheckConstraint("ck_booking_policies_daily_limit", "max_active_reservations_per_customer_per_day IS NULL OR (max_active_reservations_per_customer_per_day >= 1 AND max_active_reservations_per_customer_per_day <= 100)");
                });

            migrationBuilder.CreateIndex(
                name: "ux_booking_policies_tenant_default",
                schema: "rentals",
                table: "booking_policies",
                column: "tenant_id",
                unique: true,
                filter: "resource_type IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_booking_policies_tenant_resource_type",
                schema: "rentals",
                table: "booking_policies",
                columns: new[] { "tenant_id", "resource_type" },
                unique: true,
                filter: "resource_type IS NOT NULL");

            migrationBuilder.Sql(
                """
                INSERT INTO core.permissions (id, key, name, description, module_key, created_at)
                VALUES
                    (gen_random_uuid(), 'rentals.booking_policy.read', 'Read booking policy', 'View the tenant booking policy for every bookable resource type.', 'rentals', now()),
                    (gen_random_uuid(), 'rentals.booking_policy.write', 'Write booking policy', 'Update the tenant booking policy for every bookable resource type.', 'rentals', now())
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
                    WHERE key IN ('rentals.booking_policy.read', 'rentals.booking_policy.write'));

                DELETE FROM core.permissions
                WHERE key IN ('rentals.booking_policy.read', 'rentals.booking_policy.write');
                """);

            migrationBuilder.DropTable(
                name: "booking_policies",
                schema: "rentals");
        }
    }
}
