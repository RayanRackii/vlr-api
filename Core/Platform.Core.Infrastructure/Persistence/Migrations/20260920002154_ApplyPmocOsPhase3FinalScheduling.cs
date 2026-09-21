using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApplyPmocOsPhase3FinalScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PMOC-scoped destructive forward step. Manual WorkOrders
            // (maintenance_plan_id IS NULL) and non-PMOC tables are not deleted.
            // WorkOrderTasks cascade from os.work_orders.
            // PlanTasks cascade from pmoc.maintenance_plans.
            // WorkOrder.MaintenancePlan is Restrict, so linked OS must go first.
            migrationBuilder.Sql(
                """
                DELETE FROM os.work_orders
                WHERE maintenance_plan_id IS NOT NULL;
                """);

            migrationBuilder.Sql(
                """
                DELETE FROM pmoc.maintenance_plans;
                """);

            migrationBuilder.DropColumn(
                name: "frequency",
                schema: "pmoc",
                table: "maintenance_plans");

            migrationBuilder.DropColumn(
                name: "frequency",
                schema: "pmoc",
                table: "global_maintenance_templates");

            migrationBuilder.AddColumn<DateOnly>(
                name: "first_due_date",
                schema: "pmoc",
                table: "maintenance_plans",
                type: "date",
                nullable: false);

            migrationBuilder.AddColumn<int>(
                name: "interval_days",
                schema: "pmoc",
                table: "maintenance_plans",
                type: "integer",
                nullable: false);

            migrationBuilder.AddCheckConstraint(
                name: "ck_maintenance_plans_interval_days",
                schema: "pmoc",
                table: "maintenance_plans",
                sql: "interval_days >= 1 AND interval_days <= 3650");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_maintenance_plans_interval_days",
                schema: "pmoc",
                table: "maintenance_plans");

            migrationBuilder.DropColumn(
                name: "first_due_date",
                schema: "pmoc",
                table: "maintenance_plans");

            migrationBuilder.DropColumn(
                name: "interval_days",
                schema: "pmoc",
                table: "maintenance_plans");

            migrationBuilder.AddColumn<string>(
                name: "frequency",
                schema: "pmoc",
                table: "maintenance_plans",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "frequency",
                schema: "pmoc",
                table: "global_maintenance_templates",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.UpdateData(
                schema: "pmoc",
                table: "global_maintenance_templates",
                keyColumn: "id",
                keyValue: new Guid("6f1c2a0e-4b9d-4f3a-9c7e-1d2a3b4c5d6e"),
                column: "frequency",
                value: "Monthly");
        }
    }
}
