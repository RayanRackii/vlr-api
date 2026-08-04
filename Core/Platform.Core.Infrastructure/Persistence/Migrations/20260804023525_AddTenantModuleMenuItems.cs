using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantModuleMenuItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_module_menu_items",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    rental_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_module_menu_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_tenant_module_menu_items_rental_assets_rental_asset_id",
                        column: x => x.rental_asset_id,
                        principalSchema: "rentals",
                        principalTable: "rental_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_tenant_module_menu_items_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "core",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_module_menu_items_rental_asset_id",
                schema: "core",
                table: "tenant_module_menu_items",
                column: "rental_asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_module_menu_items_tenant_id_module_name_is_active",
                schema: "core",
                table: "tenant_module_menu_items",
                columns: new[] { "tenant_id", "module_name", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_module_menu_items_tenant_id_sort_order",
                schema: "core",
                table: "tenant_module_menu_items",
                columns: new[] { "tenant_id", "sort_order" });

            // Seed FICC: one Rentals menu item when module is active and none exist yet.
            migrationBuilder.Sql(
                """
                INSERT INTO core.tenant_module_menu_items (
                    id, tenant_id, module_name, label, sort_order, is_active,
                    rental_asset_id, created_at, updated_at
                )
                SELECT
                    gen_random_uuid(),
                    t.id,
                    'rentals',
                    'Alugar quadra',
                    10,
                    TRUE,
                    NULL,
                    NOW(),
                    NULL
                FROM core.tenants t
                INNER JOIN core.tenant_modules tm
                    ON tm.tenant_id = t.id
                   AND tm.module_name = 'rentals'
                   AND tm.is_active = TRUE
                WHERE lower(t.subdomain) = 'ficc'
                  AND NOT EXISTS (
                      SELECT 1
                      FROM core.tenant_module_menu_items m
                      WHERE m.tenant_id = t.id
                        AND m.module_name = 'rentals'
                  );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_module_menu_items",
                schema: "core");
        }
    }
}
