using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations;

/// <summary>
/// Resource planning, slice API-2: creates schema <c>planning</c> with the three planning tables
/// (<c>asset_planning_profiles</c>, <c>asset_working_hours</c>, <c>asset_allocations</c>). Additive
/// and data-safe: new tables only, no backfill, no existing row rewritten or deleted. The only
/// change to existing tables is a unique constraint on <c>(tenant_id, id)</c> of
/// <c>assets.assets</c> and <c>os.work_orders</c>; <c>id</c> is already the primary key, so the
/// constraint cannot fail on existing data. Both are the principal keys of the tenant-qualified
/// composite foreign keys of the new tables.
/// </summary>
public partial class AddAssetPlanning : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "planning");

        migrationBuilder.AddUniqueConstraint(
            name: "ak_assets_tenant_id_id",
            schema: "assets",
            table: "assets",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.AddUniqueConstraint(
            name: "ak_work_orders_tenant_id_id",
            schema: "os",
            table: "work_orders",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.CreateTable(
            name: "asset_planning_profiles",
            schema: "planning",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                is_plannable = table.Column<bool>(type: "boolean", nullable: false),
                capacity = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                hours_policy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                default_prep_minutes = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                default_cleanup_minutes = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_asset_planning_profiles", x => x.id);
                table.CheckConstraint("ck_asset_planning_profiles_capacity", "capacity >= 1");
                table.CheckConstraint("ck_asset_planning_profiles_default_prep_minutes", "default_prep_minutes >= 0");
                table.CheckConstraint("ck_asset_planning_profiles_default_cleanup_minutes", "default_cleanup_minutes >= 0");
                table.ForeignKey(
                    name: "fk_asset_planning_profiles_assets_tenant_id_asset_id",
                    columns: x => new { x.tenant_id, x.asset_id },
                    principalSchema: "assets",
                    principalTable: "assets",
                    principalColumns: new[] { "tenant_id", "id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "asset_working_hours",
            schema: "planning",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                day_of_week = table.Column<short>(type: "smallint", nullable: false),
                open_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                close_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_asset_working_hours", x => x.id);
                table.CheckConstraint("ck_asset_working_hours_day_of_week", "day_of_week >= 0 AND day_of_week <= 6");
                table.CheckConstraint("ck_asset_working_hours_close_after_open", "close_time > open_time");
                table.ForeignKey(
                    name: "fk_asset_working_hours_assets_tenant_id_asset_id",
                    columns: x => new { x.tenant_id, x.asset_id },
                    principalSchema: "assets",
                    principalTable: "assets",
                    principalColumns: new[] { "tenant_id", "id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "asset_allocations",
            schema: "planning",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                quantity = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                planned_work_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                planned_work_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                occupied_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                occupied_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Scheduled"),
                actual_start_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                actual_work_finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                actual_release_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                catalog_order_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                work_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                reference_label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                reservation_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                cancel_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                row_version = table.Column<byte[]>(type: "bytea", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_asset_allocations", x => x.id);
                table.CheckConstraint("ck_asset_allocations_quantity", "quantity >= 1");
                table.CheckConstraint("ck_asset_allocations_work_window", "planned_work_end > planned_work_start");
                table.CheckConstraint("ck_asset_allocations_occupied_covers_work", "occupied_start <= planned_work_start AND occupied_end >= planned_work_end");
                table.CheckConstraint("ck_asset_allocations_single_link", "catalog_order_item_id IS NULL OR work_order_id IS NULL");
                table.ForeignKey(
                    name: "fk_asset_allocations_assets_tenant_id_asset_id",
                    columns: x => new { x.tenant_id, x.asset_id },
                    principalSchema: "assets",
                    principalTable: "assets",
                    principalColumns: new[] { "tenant_id", "id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_asset_allocations_catalog_order_items_tenant_id_item_id",
                    columns: x => new { x.tenant_id, x.catalog_order_item_id },
                    principalSchema: "catalog",
                    principalTable: "catalog_order_items",
                    principalColumns: new[] { "tenant_id", "id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_asset_allocations_reservation_items_tenant_id_item_id",
                    columns: x => new { x.tenant_id, x.reservation_item_id },
                    principalSchema: "rentals",
                    principalTable: "reservation_items",
                    principalColumns: new[] { "tenant_id", "id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_asset_allocations_work_orders_tenant_id_work_order_id",
                    columns: x => new { x.tenant_id, x.work_order_id },
                    principalSchema: "os",
                    principalTable: "work_orders",
                    principalColumns: new[] { "tenant_id", "id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ux_asset_planning_profiles_tenant_asset",
            schema: "planning",
            table: "asset_planning_profiles",
            columns: new[] { "tenant_id", "asset_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_asset_working_hours_tenant_asset_day_open",
            schema: "planning",
            table: "asset_working_hours",
            columns: new[] { "tenant_id", "asset_id", "day_of_week", "open_time" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_asset_allocations_tenant_asset_blocking",
            schema: "planning",
            table: "asset_allocations",
            columns: new[] { "tenant_id", "asset_id" },
            filter: "status IN ('Scheduled', 'Running', 'Cleaning')");

        migrationBuilder.CreateIndex(
            name: "ix_asset_allocations_tenant_asset_occupied_start",
            schema: "planning",
            table: "asset_allocations",
            columns: new[] { "tenant_id", "asset_id", "occupied_start" });

        migrationBuilder.CreateIndex(
            name: "ix_asset_allocations_tenant_catalog_order_item",
            schema: "planning",
            table: "asset_allocations",
            columns: new[] { "tenant_id", "catalog_order_item_id" });

        migrationBuilder.CreateIndex(
            name: "ux_asset_allocations_tenant_reservation_item",
            schema: "planning",
            table: "asset_allocations",
            columns: new[] { "tenant_id", "reservation_item_id" },
            unique: true,
            filter: "reservation_item_id IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_asset_allocations_tenant_work_order",
            schema: "planning",
            table: "asset_allocations",
            columns: new[] { "tenant_id", "work_order_id" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Refuse before any destructive step when planning data exists. Allocations are history and
        // profiles / working hours are tenant configuration; neither may be dropped silently.
        // Recover from a backup or ship a forward migration instead (see docs/runbooks/database-migrations.md).
        migrationBuilder.Sql("""
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM planning.asset_allocations)
                   OR EXISTS (SELECT 1 FROM planning.asset_planning_profiles)
                   OR EXISTS (SELECT 1 FROM planning.asset_working_hours) THEN
                    RAISE EXCEPTION 'Cannot roll back asset planning while planning rows exist; recover from a backup or ship a forward migration.';
                END IF;
            END $$;
            """);

        migrationBuilder.DropTable(
            name: "asset_allocations",
            schema: "planning");

        migrationBuilder.DropTable(
            name: "asset_working_hours",
            schema: "planning");

        migrationBuilder.DropTable(
            name: "asset_planning_profiles",
            schema: "planning");

        migrationBuilder.DropUniqueConstraint(
            name: "ak_work_orders_tenant_id_id",
            schema: "os",
            table: "work_orders");

        migrationBuilder.DropUniqueConstraint(
            name: "ak_assets_tenant_id_id",
            schema: "assets",
            table: "assets");

        migrationBuilder.DropSchema(
            name: "planning");
    }
}
