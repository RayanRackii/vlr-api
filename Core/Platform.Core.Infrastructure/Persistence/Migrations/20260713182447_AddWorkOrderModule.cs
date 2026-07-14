using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkOrderModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "os");

            migrationBuilder.CreateTable(
                name: "work_orders",
                schema: "os",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    maintenance_plan_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    scheduled_date = table.Column<DateOnly>(type: "date", nullable: false),
                    completed_date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_work_orders", x => x.id);
                    table.ForeignKey(
                        name: "fk_work_orders_assets_asset_id",
                        column: x => x.asset_id,
                        principalSchema: "assets",
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_work_orders_maintenance_plans_maintenance_plan_id",
                        column: x => x.maintenance_plan_id,
                        principalSchema: "pmoc",
                        principalTable: "maintenance_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "work_order_tasks",
                schema: "os",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    input_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    configuration = table.Column<string>(type: "jsonb", nullable: true),
                    is_mandatory = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    order = table.Column<int>(type: "integer", nullable: false),
                    value = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_work_order_tasks", x => x.id);
                    table.ForeignKey(
                        name: "fk_work_order_tasks_plan_tasks_plan_task_id",
                        column: x => x.plan_task_id,
                        principalSchema: "pmoc",
                        principalTable: "plan_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_work_order_tasks_work_orders_work_order_id",
                        column: x => x.work_order_id,
                        principalSchema: "os",
                        principalTable: "work_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_work_order_tasks_plan_task_id",
                schema: "os",
                table: "work_order_tasks",
                column: "plan_task_id");

            migrationBuilder.CreateIndex(
                name: "ix_work_order_tasks_tenant_id_work_order_id_order",
                schema: "os",
                table: "work_order_tasks",
                columns: new[] { "tenant_id", "work_order_id", "order" });

            migrationBuilder.CreateIndex(
                name: "ix_work_order_tasks_work_order_id",
                schema: "os",
                table: "work_order_tasks",
                column: "work_order_id");

            migrationBuilder.CreateIndex(
                name: "ix_work_orders_asset_id",
                schema: "os",
                table: "work_orders",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_work_orders_maintenance_plan_id",
                schema: "os",
                table: "work_orders",
                column: "maintenance_plan_id");

            migrationBuilder.CreateIndex(
                name: "ix_work_orders_tenant_id_asset_id_scheduled_date",
                schema: "os",
                table: "work_orders",
                columns: new[] { "tenant_id", "asset_id", "scheduled_date" });

            migrationBuilder.CreateIndex(
                name: "ix_work_orders_tenant_id_maintenance_plan_id_asset_id_schedule",
                schema: "os",
                table: "work_orders",
                columns: new[] { "tenant_id", "maintenance_plan_id", "asset_id", "scheduled_date" });

            migrationBuilder.CreateIndex(
                name: "ix_work_orders_tenant_id_status",
                schema: "os",
                table: "work_orders",
                columns: new[] { "tenant_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "work_order_tasks",
                schema: "os");

            migrationBuilder.DropTable(
                name: "work_orders",
                schema: "os");
        }
    }
}
