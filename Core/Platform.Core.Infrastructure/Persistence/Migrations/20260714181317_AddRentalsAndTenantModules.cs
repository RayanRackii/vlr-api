using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRentalsAndTenantModules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "rentals");

            migrationBuilder.AddColumn<string>(
                name: "subdomain",
                schema: "core",
                table: "tenants",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "rental_assets",
                schema: "rentals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    total_quantity = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rental_assets", x => x.id);
                    table.ForeignKey(
                        name: "fk_rental_assets_units_unit_id",
                        column: x => x.unit_id,
                        principalSchema: "core",
                        principalTable: "units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reservations",
                schema: "rentals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    customer_whats_app = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    start_date_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    end_date_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "PendingDeposit"),
                    total_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    deposit_paid = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reservations", x => x.id);
                    table.ForeignKey(
                        name: "fk_reservations_units_unit_id",
                        column: x => x.unit_id,
                        principalSchema: "core",
                        principalTable: "units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tenant_modules",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_modules", x => x.id);
                    table.ForeignKey(
                        name: "fk_tenant_modules_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "core",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rental_pricings",
                schema: "rentals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rental_asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day_of_week = table.Column<int>(type: "integer", nullable: false),
                    start_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    end_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    price_per_hour = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    requires_deposit = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    deposit_percentage = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rental_pricings", x => x.id);
                    table.ForeignKey(
                        name: "fk_rental_pricings_rental_assets_rental_asset_id",
                        column: x => x.rental_asset_id,
                        principalSchema: "rentals",
                        principalTable: "rental_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "reservation_items",
                schema: "rentals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rental_asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    sub_total = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reservation_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_reservation_items_rental_assets_rental_asset_id",
                        column: x => x.rental_asset_id,
                        principalSchema: "rentals",
                        principalTable: "rental_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reservation_items_reservations_reservation_id",
                        column: x => x.reservation_id,
                        principalSchema: "rentals",
                        principalTable: "reservations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tenants_subdomain",
                schema: "core",
                table: "tenants",
                column: "subdomain",
                unique: true,
                filter: "subdomain IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_rental_assets_tenant_id_is_active",
                schema: "rentals",
                table: "rental_assets",
                columns: new[] { "tenant_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_rental_assets_tenant_id_unit_id_name",
                schema: "rentals",
                table: "rental_assets",
                columns: new[] { "tenant_id", "unit_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_rental_assets_unit_id",
                schema: "rentals",
                table: "rental_assets",
                column: "unit_id");

            migrationBuilder.CreateIndex(
                name: "ix_rental_pricings_rental_asset_id",
                schema: "rentals",
                table: "rental_pricings",
                column: "rental_asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_rental_pricings_tenant_id_rental_asset_id_day_of_week",
                schema: "rentals",
                table: "rental_pricings",
                columns: new[] { "tenant_id", "rental_asset_id", "day_of_week" });

            migrationBuilder.CreateIndex(
                name: "ix_reservation_items_rental_asset_id",
                schema: "rentals",
                table: "reservation_items",
                column: "rental_asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_reservation_items_reservation_id",
                schema: "rentals",
                table: "reservation_items",
                column: "reservation_id");

            migrationBuilder.CreateIndex(
                name: "ix_reservation_items_tenant_id_reservation_id",
                schema: "rentals",
                table: "reservation_items",
                columns: new[] { "tenant_id", "reservation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_reservations_tenant_id_start_date_time_end_date_time",
                schema: "rentals",
                table: "reservations",
                columns: new[] { "tenant_id", "start_date_time", "end_date_time" });

            migrationBuilder.CreateIndex(
                name: "ix_reservations_tenant_id_status",
                schema: "rentals",
                table: "reservations",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_reservations_unit_id",
                schema: "rentals",
                table: "reservations",
                column: "unit_id");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_modules_tenant_id_module_name",
                schema: "core",
                table: "tenant_modules",
                columns: new[] { "tenant_id", "module_name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rental_pricings",
                schema: "rentals");

            migrationBuilder.DropTable(
                name: "reservation_items",
                schema: "rentals");

            migrationBuilder.DropTable(
                name: "tenant_modules",
                schema: "core");

            migrationBuilder.DropTable(
                name: "rental_assets",
                schema: "rentals");

            migrationBuilder.DropTable(
                name: "reservations",
                schema: "rentals");

            migrationBuilder.DropIndex(
                name: "ix_tenants_subdomain",
                schema: "core",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "subdomain",
                schema: "core",
                table: "tenants");
        }
    }
}
