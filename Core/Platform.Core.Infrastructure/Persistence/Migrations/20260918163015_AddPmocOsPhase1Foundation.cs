using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPmocOsPhase1Foundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_work_orders_maintenance_plans_maintenance_plan_id",
                schema: "os",
                table: "work_orders");

            migrationBuilder.AddColumn<string>(
                name: "source_plan_name",
                schema: "os",
                table: "work_orders",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "auto_generate_enabled",
                schema: "pmoc",
                table: "maintenance_plans",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.Sql(
                """
                ALTER TABLE pmoc.maintenance_plans
                  ALTER COLUMN auto_generate_enabled SET DEFAULT false;
                """);

            migrationBuilder.AddColumn<string>(
                name: "origin_kind",
                schema: "pmoc",
                table: "maintenance_plans",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Custom");

            migrationBuilder.AddColumn<Guid>(
                name: "source_template_id",
                schema: "pmoc",
                table: "maintenance_plans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "source_template_version",
                schema: "pmoc",
                table: "maintenance_plans",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "library_key",
                schema: "pmoc",
                table: "global_maintenance_templates",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "pmoc-ar-condicionado-anvisa-nr10");

            migrationBuilder.AddColumn<string>(
                name: "source_references",
                schema: "pmoc",
                table: "global_maintenance_templates",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                schema: "pmoc",
                table: "global_maintenance_templates",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Published");

            migrationBuilder.AddColumn<int>(
                name: "version",
                schema: "pmoc",
                table: "global_maintenance_templates",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.UpdateData(
                schema: "pmoc",
                table: "global_maintenance_templates",
                keyColumn: "id",
                keyValue: new Guid("6f1c2a0e-4b9d-4f3a-9c7e-1d2a3b4c5d6e"),
                columns: new[] { "library_key", "source_references", "status", "version" },
                values: new object[] { "pmoc-ar-condicionado-anvisa-nr10", "Lei 13.589/2018; Resolução Anvisa RE 09; NR-10", "Published", 1 });

            migrationBuilder.Sql(
                """
                ALTER TABLE pmoc.global_maintenance_templates
                  ALTER COLUMN library_key DROP DEFAULT,
                  ALTER COLUMN status DROP DEFAULT,
                  ALTER COLUMN version DROP DEFAULT;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_plans_source_template_id",
                schema: "pmoc",
                table: "maintenance_plans",
                column: "source_template_id");

            migrationBuilder.CreateIndex(
                name: "ix_global_maintenance_templates_library_key_version",
                schema: "pmoc",
                table: "global_maintenance_templates",
                columns: new[] { "library_key", "version" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_maintenance_plans_global_maintenance_templates_source_templ",
                schema: "pmoc",
                table: "maintenance_plans",
                column: "source_template_id",
                principalSchema: "pmoc",
                principalTable: "global_maintenance_templates",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_work_orders_maintenance_plans_maintenance_plan_id",
                schema: "os",
                table: "work_orders",
                column: "maintenance_plan_id",
                principalSchema: "pmoc",
                principalTable: "maintenance_plans",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(
                """
                UPDATE os.work_orders w
                SET source_plan_name = p.name
                FROM pmoc.maintenance_plans p
                WHERE w.maintenance_plan_id = p.id
                  AND w.source_plan_name IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_maintenance_plans_global_maintenance_templates_source_templ",
                schema: "pmoc",
                table: "maintenance_plans");

            migrationBuilder.DropForeignKey(
                name: "fk_work_orders_maintenance_plans_maintenance_plan_id",
                schema: "os",
                table: "work_orders");

            migrationBuilder.DropIndex(
                name: "ix_maintenance_plans_source_template_id",
                schema: "pmoc",
                table: "maintenance_plans");

            migrationBuilder.DropIndex(
                name: "ix_global_maintenance_templates_library_key_version",
                schema: "pmoc",
                table: "global_maintenance_templates");

            migrationBuilder.DropColumn(
                name: "source_plan_name",
                schema: "os",
                table: "work_orders");

            migrationBuilder.DropColumn(
                name: "auto_generate_enabled",
                schema: "pmoc",
                table: "maintenance_plans");

            migrationBuilder.DropColumn(
                name: "origin_kind",
                schema: "pmoc",
                table: "maintenance_plans");

            migrationBuilder.DropColumn(
                name: "source_template_id",
                schema: "pmoc",
                table: "maintenance_plans");

            migrationBuilder.DropColumn(
                name: "source_template_version",
                schema: "pmoc",
                table: "maintenance_plans");

            migrationBuilder.DropColumn(
                name: "library_key",
                schema: "pmoc",
                table: "global_maintenance_templates");

            migrationBuilder.DropColumn(
                name: "source_references",
                schema: "pmoc",
                table: "global_maintenance_templates");

            migrationBuilder.DropColumn(
                name: "status",
                schema: "pmoc",
                table: "global_maintenance_templates");

            migrationBuilder.DropColumn(
                name: "version",
                schema: "pmoc",
                table: "global_maintenance_templates");

            migrationBuilder.AddForeignKey(
                name: "fk_work_orders_maintenance_plans_maintenance_plan_id",
                schema: "os",
                table: "work_orders",
                column: "maintenance_plan_id",
                principalSchema: "pmoc",
                principalTable: "maintenance_plans",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
