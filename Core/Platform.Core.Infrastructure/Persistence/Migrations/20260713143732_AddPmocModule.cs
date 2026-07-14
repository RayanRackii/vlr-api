using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPmocModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "pmoc");

            migrationBuilder.CreateTable(
                name: "maintenance_plans",
                schema: "pmoc",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    frequency = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    asset_category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_maintenance_plans", x => x.id);
                    table.ForeignKey(
                        name: "fk_maintenance_plans_asset_categories_asset_category_id",
                        column: x => x.asset_category_id,
                        principalSchema: "assets",
                        principalTable: "asset_categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_maintenance_plans_units_unit_id",
                        column: x => x.unit_id,
                        principalSchema: "core",
                        principalTable: "units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "plan_tasks",
                schema: "pmoc",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    maintenance_plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    input_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    is_mandatory = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    order = table.Column<int>(type: "integer", nullable: false),
                    configuration = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plan_tasks", x => x.id);
                    table.ForeignKey(
                        name: "fk_plan_tasks_maintenance_plans_maintenance_plan_id",
                        column: x => x.maintenance_plan_id,
                        principalSchema: "pmoc",
                        principalTable: "maintenance_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_plans_asset_category_id",
                schema: "pmoc",
                table: "maintenance_plans",
                column: "asset_category_id");

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_plans_tenant_id_asset_category_id",
                schema: "pmoc",
                table: "maintenance_plans",
                columns: new[] { "tenant_id", "asset_category_id" });

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_plans_tenant_id_name",
                schema: "pmoc",
                table: "maintenance_plans",
                columns: new[] { "tenant_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_plans_tenant_id_unit_id",
                schema: "pmoc",
                table: "maintenance_plans",
                columns: new[] { "tenant_id", "unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_plans_unit_id",
                schema: "pmoc",
                table: "maintenance_plans",
                column: "unit_id");

            migrationBuilder.CreateIndex(
                name: "ix_plan_tasks_maintenance_plan_id",
                schema: "pmoc",
                table: "plan_tasks",
                column: "maintenance_plan_id");

            migrationBuilder.CreateIndex(
                name: "ix_plan_tasks_tenant_id_maintenance_plan_id_order",
                schema: "pmoc",
                table: "plan_tasks",
                columns: new[] { "tenant_id", "maintenance_plan_id", "order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plan_tasks",
                schema: "pmoc");

            migrationBuilder.DropTable(
                name: "maintenance_plans",
                schema: "pmoc");
        }
    }
}
