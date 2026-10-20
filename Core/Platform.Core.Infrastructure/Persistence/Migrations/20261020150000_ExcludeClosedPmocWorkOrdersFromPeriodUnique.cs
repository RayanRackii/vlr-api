using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Platform.Core.Infrastructure.Persistence;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261020150000_ExcludeClosedPmocWorkOrdersFromPeriodUnique")]
public partial class ExcludeClosedPmocWorkOrdersFromPeriodUnique : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ux_os_work_orders_pmoc_period",
            schema: "os",
            table: "work_orders");

        migrationBuilder.CreateIndex(
            name: "ux_os_work_orders_pmoc_period",
            schema: "os",
            table: "work_orders",
            columns: new[] { "tenant_id", "maintenance_plan_id", "asset_id", "scheduled_date" },
            unique: true,
            filter: "maintenance_plan_id IS NOT NULL AND status IN ('Pending', 'InProgress')");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ux_os_work_orders_pmoc_period",
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
}
