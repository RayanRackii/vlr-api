using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRentalsScheduleAndLayouts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "allowed_duration_minutes",
                schema: "rentals",
                table: "rental_assets",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "close_time",
                schema: "rentals",
                table: "rental_assets",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "open_time",
                schema: "rentals",
                table: "rental_assets",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "schedule_policy",
                schema: "rentals",
                table: "rental_assets",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "SlotGrid");

            migrationBuilder.CreateTable(
                name: "layouts",
                schema: "rentals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_layouts", x => x.id);
                    table.ForeignKey(
                        name: "fk_layouts_units_unit_id",
                        column: x => x.unit_id,
                        principalSchema: "core",
                        principalTable: "units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "occupancy_kinds",
                schema: "rentals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    color_hex = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    is_bookable_by_customer = table.Column<bool>(type: "boolean", nullable: false),
                    blocks_capacity = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_occupancy_kinds", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "layout_items",
                schema: "rentals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    layout_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rental_asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    x_percent = table.Column<double>(type: "double precision", nullable: false),
                    y_percent = table.Column<double>(type: "double precision", nullable: false),
                    width_percent = table.Column<double>(type: "double precision", nullable: false),
                    height_percent = table.Column<double>(type: "double precision", nullable: false),
                    z_index = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_layout_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_layout_items_layouts_layout_id",
                        column: x => x.layout_id,
                        principalSchema: "rentals",
                        principalTable: "layouts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_layout_items_rental_assets_rental_asset_id",
                        column: x => x.rental_asset_id,
                        principalSchema: "rentals",
                        principalTable: "rental_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "schedule_templates",
                schema: "rentals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rental_asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day_of_week = table.Column<int>(type: "integer", nullable: false),
                    start_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    end_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    occupancy_kind_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_schedule_templates", x => x.id);
                    table.ForeignKey(
                        name: "fk_schedule_templates_occupancy_kinds_occupancy_kind_id",
                        column: x => x.occupancy_kind_id,
                        principalSchema: "rentals",
                        principalTable: "occupancy_kinds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_schedule_templates_rental_assets_rental_asset_id",
                        column: x => x.rental_asset_id,
                        principalSchema: "rentals",
                        principalTable: "rental_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "slots",
                schema: "rentals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rental_asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    start_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    end_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    occupancy_kind_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Available"),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_template_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_slots", x => x.id);
                    table.ForeignKey(
                        name: "fk_slots_occupancy_kinds_occupancy_kind_id",
                        column: x => x.occupancy_kind_id,
                        principalSchema: "rentals",
                        principalTable: "occupancy_kinds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_slots_rental_assets_rental_asset_id",
                        column: x => x.rental_asset_id,
                        principalSchema: "rentals",
                        principalTable: "rental_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_slots_reservations_reservation_id",
                        column: x => x.reservation_id,
                        principalSchema: "rentals",
                        principalTable: "reservations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_slots_schedule_templates_source_template_id",
                        column: x => x.source_template_id,
                        principalSchema: "rentals",
                        principalTable: "schedule_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_layout_items_layout_id_rental_asset_id",
                schema: "rentals",
                table: "layout_items",
                columns: new[] { "layout_id", "rental_asset_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_layout_items_rental_asset_id",
                schema: "rentals",
                table: "layout_items",
                column: "rental_asset_id");

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
                name: "ix_occupancy_kinds_tenant_id_is_active_sort_order",
                schema: "rentals",
                table: "occupancy_kinds",
                columns: new[] { "tenant_id", "is_active", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_occupancy_kinds_tenant_id_key",
                schema: "rentals",
                table: "occupancy_kinds",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_schedule_templates_occupancy_kind_id",
                schema: "rentals",
                table: "schedule_templates",
                column: "occupancy_kind_id");

            migrationBuilder.CreateIndex(
                name: "ix_schedule_templates_rental_asset_id",
                schema: "rentals",
                table: "schedule_templates",
                column: "rental_asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_schedule_templates_tenant_id_rental_asset_id_day_of_week_st",
                schema: "rentals",
                table: "schedule_templates",
                columns: new[] { "tenant_id", "rental_asset_id", "day_of_week", "start_time" });

            migrationBuilder.CreateIndex(
                name: "ix_slots_occupancy_kind_id",
                schema: "rentals",
                table: "slots",
                column: "occupancy_kind_id");

            migrationBuilder.CreateIndex(
                name: "ix_slots_rental_asset_id",
                schema: "rentals",
                table: "slots",
                column: "rental_asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_slots_reservation_id",
                schema: "rentals",
                table: "slots",
                column: "reservation_id");

            migrationBuilder.CreateIndex(
                name: "ix_slots_source_template_id",
                schema: "rentals",
                table: "slots",
                column: "source_template_id");

            migrationBuilder.CreateIndex(
                name: "ix_slots_tenant_id_date_status",
                schema: "rentals",
                table: "slots",
                columns: new[] { "tenant_id", "date", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_slots_tenant_id_rental_asset_id_date_start_time",
                schema: "rentals",
                table: "slots",
                columns: new[] { "tenant_id", "rental_asset_id", "date", "start_time" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "layout_items",
                schema: "rentals");

            migrationBuilder.DropTable(
                name: "slots",
                schema: "rentals");

            migrationBuilder.DropTable(
                name: "layouts",
                schema: "rentals");

            migrationBuilder.DropTable(
                name: "schedule_templates",
                schema: "rentals");

            migrationBuilder.DropTable(
                name: "occupancy_kinds",
                schema: "rentals");

            migrationBuilder.DropColumn(
                name: "allowed_duration_minutes",
                schema: "rentals",
                table: "rental_assets");

            migrationBuilder.DropColumn(
                name: "close_time",
                schema: "rentals",
                table: "rental_assets");

            migrationBuilder.DropColumn(
                name: "open_time",
                schema: "rentals",
                table: "rental_assets");

            migrationBuilder.DropColumn(
                name: "schedule_policy",
                schema: "rentals",
                table: "rental_assets");
        }
    }
}
