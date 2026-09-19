using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPmocOsWorkOrderPeriodUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_work_orders_tenant_id_maintenance_plan_id_asset_id_schedule",
                schema: "os",
                table: "work_orders");

            migrationBuilder.CreateIndex(
                name: "ux_os_work_orders_pmoc_period",
                schema: "os",
                table: "work_orders",
                columns: new[] { "tenant_id", "maintenance_plan_id", "asset_id", "scheduled_date" },
                unique: true,
                filter: "maintenance_plan_id IS NOT NULL AND status <> 'Canceled'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_os_work_orders_pmoc_period",
                schema: "os",
                table: "work_orders");

            migrationBuilder.CreateIndex(
                name: "ix_work_orders_tenant_id_maintenance_plan_id_asset_id_schedule",
                schema: "os",
                table: "work_orders",
                columns: new[] { "tenant_id", "maintenance_plan_id", "asset_id", "scheduled_date" });
        }
    }
}
