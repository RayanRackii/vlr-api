using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExtendRentalMapsAndVisualAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Audit before changing any legacy FK. Never repair tenant ownership implicitly.
            migrationBuilder.Sql("""
                LOCK TABLE rentals.layouts, rentals.layout_items, rentals.rental_assets, core.units, assets.assets
                    IN SHARE ROW EXCLUSIVE MODE;
                DO $preflight$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM rentals.layouts l
                        LEFT JOIN core.tenants t ON t.id = l.tenant_id
                        LEFT JOIN core.units u ON u.id = l.unit_id AND u.tenant_id = l.tenant_id
                        WHERE t.id IS NULL OR (l.unit_id IS NOT NULL AND u.id IS NULL)
                    ) OR EXISTS (
                        SELECT 1 FROM rentals.rental_assets r
                        LEFT JOIN core.tenants t ON t.id = r.tenant_id
                        LEFT JOIN assets.assets a ON a.id = r.asset_id AND a.tenant_id = r.tenant_id
                        WHERE t.id IS NULL OR a.id IS NULL
                    ) OR EXISTS (
                        SELECT 1 FROM rentals.layout_items i
                        LEFT JOIN rentals.layouts l ON l.id = i.layout_id AND l.tenant_id = i.tenant_id
                        LEFT JOIN rentals.rental_assets r ON r.id = i.rental_asset_id AND r.tenant_id = i.tenant_id
                        LEFT JOIN assets.assets a ON a.id = r.asset_id AND a.tenant_id = i.tenant_id
                        WHERE l.id IS NULL OR r.id IS NULL OR a.id IS NULL
                            OR (l.unit_id IS NOT NULL AND l.unit_id IS DISTINCT FROM a.unit_id)
                    ) THEN
                        RAISE EXCEPTION 'BOOKING_MAP_PREFLIGHT_FAILED: Legacy tenant/unit links are inconsistent. Audit and explicitly repair ownership before retrying; no data was changed.';
                    END IF;
                END;
                $preflight$;
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_layout_items_layouts_layout_id",
                schema: "rentals",
                table: "layout_items");

            migrationBuilder.DropForeignKey(
                name: "fk_layout_items_rental_assets_rental_asset_id",
                schema: "rentals",
                table: "layout_items");

            migrationBuilder.DropForeignKey(
                name: "fk_layouts_units_unit_id",
                schema: "rentals",
                table: "layouts");

            migrationBuilder.DropIndex(
                name: "ix_layouts_tenant_id_is_active",
                schema: "rentals",
                table: "layouts");

            migrationBuilder.DropIndex(
                name: "ix_layouts_unit_id",
                schema: "rentals",
                table: "layouts");

            migrationBuilder.DropIndex(
                name: "ix_layout_items_rental_asset_id",
                schema: "rentals",
                table: "layout_items");

            migrationBuilder.AddColumn<int>(
                name: "capacity_people",
                schema: "rentals",
                table: "rental_assets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "visual_template_revision_id",
                schema: "rentals",
                table: "rental_assets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "sort_order",
                schema: "rentals",
                table: "layouts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "version",
                schema: "rentals",
                table: "layouts",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<bool>(
                name: "lock_aspect_ratio",
                schema: "rentals",
                table: "layout_items",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<short>(
                name: "rotation",
                schema: "rentals",
                table: "layout_items",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_units_tenant_id_id",
                schema: "core",
                table: "units",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_rental_assets_tenant_id_id",
                schema: "rentals",
                table: "rental_assets",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_rental_layouts_tenant_id_id",
                schema: "rentals",
                table: "layouts",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateTable(
                name: "booking_map_settings",
                schema: "rentals",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    available_color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    taken_color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    unavailable_color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    selected_color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    show_price_on_block = table.Column<bool>(type: "boolean", nullable: false),
                    time_filter_mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    default_view = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    default_layout_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_booking_map_settings", x => x.tenant_id);
                    table.ForeignKey(
                        name: "fk_booking_map_settings_rental_layouts_tenant_id_default_layou",
                        columns: x => new { x.tenant_id, x.default_layout_id },
                        principalSchema: "rentals",
                        principalTable: "layouts",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_booking_map_settings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "core",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "layout_decorations",
                schema: "rentals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    layout_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    icon_key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    x_percent = table.Column<double>(type: "double precision", nullable: false),
                    y_percent = table.Column<double>(type: "double precision", nullable: false),
                    width_percent = table.Column<double>(type: "double precision", nullable: false),
                    height_percent = table.Column<double>(type: "double precision", nullable: false),
                    rotation = table.Column<short>(type: "smallint", nullable: false),
                    z_index = table.Column<int>(type: "integer", nullable: false),
                    style_json = table.Column<string>(type: "jsonb", nullable: false),
                    geometry_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_layout_decorations", x => x.id);
                    table.ForeignKey(
                        name: "fk_layout_decorations_rental_layouts_tenant_id_layout_id",
                        columns: x => new { x.tenant_id, x.layout_id },
                        principalSchema: "rentals",
                        principalTable: "layouts",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_layout_decorations_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "core",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_rental_assets_visual_template_revision_id",
                schema: "rentals",
                table: "rental_assets",
                column: "visual_template_revision_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_rental_assets_capacity_people",
                schema: "rentals",
                table: "rental_assets",
                sql: "capacity_people IS NULL OR (capacity_people > 0 AND capacity_people <= 100000)");

            migrationBuilder.CreateIndex(
                name: "ix_layouts_tenant_id_is_active_sort_order",
                schema: "rentals",
                table: "layouts",
                columns: new[] { "tenant_id", "is_active", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_layouts_tenant_id_unit_id",
                schema: "rentals",
                table: "layouts",
                columns: new[] { "tenant_id", "unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_layout_items_tenant_id_layout_id",
                schema: "rentals",
                table: "layout_items",
                columns: new[] { "tenant_id", "layout_id" });

            migrationBuilder.CreateIndex(
                name: "ix_layout_items_tenant_id_rental_asset_id",
                schema: "rentals",
                table: "layout_items",
                columns: new[] { "tenant_id", "rental_asset_id" });

            migrationBuilder.CreateIndex(
                name: "ix_booking_map_settings_tenant_id_default_layout_id",
                schema: "rentals",
                table: "booking_map_settings",
                columns: new[] { "tenant_id", "default_layout_id" });

            migrationBuilder.CreateIndex(
                name: "ix_layout_decorations_tenant_id_layout_id_z_index",
                schema: "rentals",
                table: "layout_decorations",
                columns: new[] { "tenant_id", "layout_id", "z_index" });

            migrationBuilder.AddForeignKey(
                name: "fk_layout_items_layouts_tenant_id_layout_id",
                schema: "rentals",
                table: "layout_items",
                columns: new[] { "tenant_id", "layout_id" },
                principalSchema: "rentals",
                principalTable: "layouts",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_layout_items_rental_assets_tenant_id_rental_asset_id",
                schema: "rentals",
                table: "layout_items",
                columns: new[] { "tenant_id", "rental_asset_id" },
                principalSchema: "rentals",
                principalTable: "rental_assets",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_layout_items_tenants_tenant_id",
                schema: "rentals",
                table: "layout_items",
                column: "tenant_id",
                principalSchema: "core",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_layouts_tenants_tenant_id",
                schema: "rentals",
                table: "layouts",
                column: "tenant_id",
                principalSchema: "core",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_layouts_units_tenant_id_unit_id",
                schema: "rentals",
                table: "layouts",
                columns: new[] { "tenant_id", "unit_id" },
                principalSchema: "core",
                principalTable: "units",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_rental_assets_resource_visual_template_revisions_visual_tem",
                schema: "rentals",
                table: "rental_assets",
                column: "visual_template_revision_id",
                principalSchema: "core",
                principalTable: "resource_visual_template_revisions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_rental_assets_tenants_tenant_id",
                schema: "rentals",
                table: "rental_assets",
                column: "tenant_id",
                principalSchema: "core",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_layout_items_layouts_tenant_id_layout_id",
                schema: "rentals",
                table: "layout_items");

            migrationBuilder.DropForeignKey(
                name: "fk_layout_items_rental_assets_tenant_id_rental_asset_id",
                schema: "rentals",
                table: "layout_items");

            migrationBuilder.DropForeignKey(
                name: "fk_layout_items_tenants_tenant_id",
                schema: "rentals",
                table: "layout_items");

            migrationBuilder.DropForeignKey(
                name: "fk_layouts_tenants_tenant_id",
                schema: "rentals",
                table: "layouts");

            migrationBuilder.DropForeignKey(
                name: "fk_layouts_units_tenant_id_unit_id",
                schema: "rentals",
                table: "layouts");

            migrationBuilder.DropForeignKey(
                name: "fk_rental_assets_resource_visual_template_revisions_visual_tem",
                schema: "rentals",
                table: "rental_assets");

            migrationBuilder.DropForeignKey(
                name: "fk_rental_assets_tenants_tenant_id",
                schema: "rentals",
                table: "rental_assets");

            migrationBuilder.DropTable(
                name: "booking_map_settings",
                schema: "rentals");

            migrationBuilder.DropTable(
                name: "layout_decorations",
                schema: "rentals");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_units_tenant_id_id",
                schema: "core",
                table: "units");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_rental_assets_tenant_id_id",
                schema: "rentals",
                table: "rental_assets");

            migrationBuilder.DropIndex(
                name: "ix_rental_assets_visual_template_revision_id",
                schema: "rentals",
                table: "rental_assets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_rental_assets_capacity_people",
                schema: "rentals",
                table: "rental_assets");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_rental_layouts_tenant_id_id",
                schema: "rentals",
                table: "layouts");

            migrationBuilder.DropIndex(
                name: "ix_layouts_tenant_id_is_active_sort_order",
                schema: "rentals",
                table: "layouts");

            migrationBuilder.DropIndex(
                name: "ix_layouts_tenant_id_unit_id",
                schema: "rentals",
                table: "layouts");

            migrationBuilder.DropIndex(
                name: "ix_layout_items_tenant_id_layout_id",
                schema: "rentals",
                table: "layout_items");

            migrationBuilder.DropIndex(
                name: "ix_layout_items_tenant_id_rental_asset_id",
                schema: "rentals",
                table: "layout_items");

            migrationBuilder.DropColumn(
                name: "capacity_people",
                schema: "rentals",
                table: "rental_assets");

            migrationBuilder.DropColumn(
                name: "visual_template_revision_id",
                schema: "rentals",
                table: "rental_assets");

            migrationBuilder.DropColumn(
                name: "sort_order",
                schema: "rentals",
                table: "layouts");

            migrationBuilder.DropColumn(
                name: "version",
                schema: "rentals",
                table: "layouts");

            migrationBuilder.DropColumn(
                name: "lock_aspect_ratio",
                schema: "rentals",
                table: "layout_items");

            migrationBuilder.DropColumn(
                name: "rotation",
                schema: "rentals",
                table: "layout_items");

            migrationBuilder.CreateIndex(
                name: "ix_layouts_tenant_id_is_active",
                schema: "rentals",
                table: "layouts",
                columns: new[] { "tenant_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_layouts_unit_id",
                schema: "rentals",
                table: "layouts",
                column: "unit_id");

            migrationBuilder.CreateIndex(
                name: "ix_layout_items_rental_asset_id",
                schema: "rentals",
                table: "layout_items",
                column: "rental_asset_id");

            migrationBuilder.AddForeignKey(
                name: "fk_layout_items_layouts_layout_id",
                schema: "rentals",
                table: "layout_items",
                column: "layout_id",
                principalSchema: "rentals",
                principalTable: "layouts",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_layout_items_rental_assets_rental_asset_id",
                schema: "rentals",
                table: "layout_items",
                column: "rental_asset_id",
                principalSchema: "rentals",
                principalTable: "rental_assets",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_layouts_units_unit_id",
                schema: "rentals",
                table: "layouts",
                column: "unit_id",
                principalSchema: "core",
                principalTable: "units",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
