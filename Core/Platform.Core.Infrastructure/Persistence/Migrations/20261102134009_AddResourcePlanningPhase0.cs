using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddResourcePlanningPhase0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "customer_whats_app",
                schema: "rentals",
                table: "reservations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<string>(
                name: "customer_name",
                schema: "rentals",
                table: "reservations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<Guid>(
                name: "customer_id",
                schema: "rentals",
                table: "reservations",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "purpose",
                schema: "rentals",
                table: "reservations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "CustomerBooking");

            migrationBuilder.AddColumn<int>(
                name: "resource_requirements_version",
                schema: "catalog",
                table: "catalog_products",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "planning_revision",
                schema: "catalog",
                table: "catalog_orders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "resource_requirements_version",
                schema: "catalog",
                table: "catalog_orders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_reservation_items_tenant_id_id",
                schema: "rentals",
                table: "reservation_items",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_catalog_order_items_tenant_id_id",
                schema: "catalog",
                table: "catalog_order_items",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateTable(
                name: "product_resource_requirements",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rental_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    asset_category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resource_quantity = table.Column<int>(type: "integer", nullable: false),
                    duration_minutes_per_unit = table.Column<int>(type: "integer", nullable: false),
                    prep_minutes = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    cleanup_minutes = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_resource_requirements", x => x.id);
                    table.UniqueConstraint("ak_catalog_product_resource_requirements_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_product_resource_requirements_exactly_one_selector", "(rental_asset_id IS NOT NULL AND asset_category_id IS NULL) OR (rental_asset_id IS NULL AND asset_category_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_product_resource_requirements_catalog_products_tenant_id_pr",
                        columns: x => new { x.tenant_id, x.product_id },
                        principalSchema: "catalog",
                        principalTable: "catalog_products",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_product_resource_requirements_rental_assets_tenant_id_renta",
                        columns: x => new { x.tenant_id, x.rental_asset_id },
                        principalSchema: "rentals",
                        principalTable: "rental_assets",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_item_resource_requirements",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_requirement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rental_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    asset_category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resource_quantity = table.Column<int>(type: "integer", nullable: false),
                    duration_minutes_per_unit = table.Column<int>(type: "integer", nullable: false),
                    prep_minutes = table.Column<int>(type: "integer", nullable: false),
                    cleanup_minutes = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_item_resource_requirements", x => x.id);
                    table.UniqueConstraint("ak_catalog_order_item_resource_requirements_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_order_item_resource_requirements_exactly_one_selector", "(rental_asset_id IS NOT NULL AND asset_category_id IS NULL) OR (rental_asset_id IS NULL AND asset_category_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_order_item_resource_requirements_catalog_order_items_tenant",
                        columns: x => new { x.tenant_id, x.order_item_id },
                        principalSchema: "catalog",
                        principalTable: "catalog_order_items",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_order_item_resource_requirements_catalog_product_resource_r",
                        columns: x => new { x.tenant_id, x.source_requirement_id },
                        principalSchema: "catalog",
                        principalTable: "product_resource_requirements",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_order_item_resource_requirements_rental_assets_tenant_id_re",
                        columns: x => new { x.tenant_id, x.rental_asset_id },
                        principalSchema: "rentals",
                        principalTable: "rental_assets",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_item_resource_allocations",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requirement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    planning_revision = table.Column<int>(type: "integer", nullable: false),
                    is_current = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    allocated_quantity = table.Column<int>(type: "integer", nullable: false),
                    rental_asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    planned_work_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    planned_work_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Scheduled"),
                    actual_start_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    actual_work_finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    actual_release_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_item_resource_allocations", x => x.id);
                    table.ForeignKey(
                        name: "fk_order_item_resource_allocations_catalog_order_item_resource",
                        columns: x => new { x.tenant_id, x.requirement_id },
                        principalSchema: "catalog",
                        principalTable: "order_item_resource_requirements",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_order_item_resource_allocations_rental_assets_tenant_id_ren",
                        columns: x => new { x.tenant_id, x.rental_asset_id },
                        principalSchema: "rentals",
                        principalTable: "rental_assets",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_order_item_resource_allocations_reservation_items_tenant_id",
                        columns: x => new { x.tenant_id, x.reservation_item_id },
                        principalSchema: "rentals",
                        principalTable: "reservation_items",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_reservations_tenant_id_purpose_status_end_date_time",
                schema: "rentals",
                table: "reservations",
                columns: new[] { "tenant_id", "purpose", "status", "end_date_time" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_reservations_customer_booking_requires_customer",
                schema: "rentals",
                table: "reservations",
                sql: "purpose = 'InternalUse' OR (customer_id IS NOT NULL AND customer_name IS NOT NULL AND customer_whats_app IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_order_item_resource_allocations_tenant_id_rental_asset_id",
                schema: "catalog",
                table: "order_item_resource_allocations",
                columns: new[] { "tenant_id", "rental_asset_id" });

            migrationBuilder.CreateIndex(
                name: "ix_order_item_resource_allocations_tenant_id_reservation_item_",
                schema: "catalog",
                table: "order_item_resource_allocations",
                columns: new[] { "tenant_id", "reservation_item_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_order_item_resource_allocations_current_occurrence",
                schema: "catalog",
                table: "order_item_resource_allocations",
                columns: new[] { "tenant_id", "requirement_id", "sequence" },
                unique: true,
                filter: "is_current = true");

            migrationBuilder.CreateIndex(
                name: "ix_order_item_resource_requirements_tenant_id_order_item_id",
                schema: "catalog",
                table: "order_item_resource_requirements",
                columns: new[] { "tenant_id", "order_item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_order_item_resource_requirements_tenant_id_rental_asset_id",
                schema: "catalog",
                table: "order_item_resource_requirements",
                columns: new[] { "tenant_id", "rental_asset_id" });

            migrationBuilder.CreateIndex(
                name: "ix_order_item_resource_requirements_tenant_id_source_requireme",
                schema: "catalog",
                table: "order_item_resource_requirements",
                columns: new[] { "tenant_id", "source_requirement_id" });

            migrationBuilder.CreateIndex(
                name: "ix_product_resource_requirements_tenant_id_product_id_is_active",
                schema: "catalog",
                table: "product_resource_requirements",
                columns: new[] { "tenant_id", "product_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_product_resource_requirements_tenant_id_rental_asset_id",
                schema: "catalog",
                table: "product_resource_requirements",
                columns: new[] { "tenant_id", "rental_asset_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_item_resource_allocations",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "order_item_resource_requirements",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "product_resource_requirements",
                schema: "catalog");

            migrationBuilder.DropIndex(
                name: "ix_reservations_tenant_id_purpose_status_end_date_time",
                schema: "rentals",
                table: "reservations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_reservations_customer_booking_requires_customer",
                schema: "rentals",
                table: "reservations");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_reservation_items_tenant_id_id",
                schema: "rentals",
                table: "reservation_items");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_catalog_order_items_tenant_id_id",
                schema: "catalog",
                table: "catalog_order_items");

            migrationBuilder.DropColumn(
                name: "purpose",
                schema: "rentals",
                table: "reservations");

            migrationBuilder.DropColumn(
                name: "resource_requirements_version",
                schema: "catalog",
                table: "catalog_products");

            migrationBuilder.DropColumn(
                name: "planning_revision",
                schema: "catalog",
                table: "catalog_orders");

            migrationBuilder.DropColumn(
                name: "resource_requirements_version",
                schema: "catalog",
                table: "catalog_orders");

            migrationBuilder.AlterColumn<string>(
                name: "customer_whats_app",
                schema: "rentals",
                table: "reservations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "customer_name",
                schema: "rentals",
                table: "reservations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "customer_id",
                schema: "rentals",
                table: "reservations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
