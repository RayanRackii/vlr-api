using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMaintenancePlanAssetTarget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "asset_category_id",
                schema: "pmoc",
                table: "maintenance_plans",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "asset_id",
                schema: "pmoc",
                table: "maintenance_plans",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_plans_asset_id",
                schema: "pmoc",
                table: "maintenance_plans",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_plans_tenant_id_asset_id",
                schema: "pmoc",
                table: "maintenance_plans",
                columns: new[] { "tenant_id", "asset_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_maintenance_plans_one_target",
                schema: "pmoc",
                table: "maintenance_plans",
                sql: "(asset_id IS NULL AND asset_category_id IS NOT NULL) OR (asset_id IS NOT NULL AND asset_category_id IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "fk_maintenance_plans_assets_asset_id",
                schema: "pmoc",
                table: "maintenance_plans",
                column: "asset_id",
                principalSchema: "assets",
                principalTable: "assets",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_maintenance_plans_assets_asset_id",
                schema: "pmoc",
                table: "maintenance_plans");

            migrationBuilder.DropIndex(
                name: "ix_maintenance_plans_asset_id",
                schema: "pmoc",
                table: "maintenance_plans");

            migrationBuilder.DropIndex(
                name: "ix_maintenance_plans_tenant_id_asset_id",
                schema: "pmoc",
                table: "maintenance_plans");

            migrationBuilder.DropCheckConstraint(
                name: "ck_maintenance_plans_one_target",
                schema: "pmoc",
                table: "maintenance_plans");

            migrationBuilder.DropColumn(
                name: "asset_id",
                schema: "pmoc",
                table: "maintenance_plans");

            migrationBuilder.AlterColumn<Guid>(
                name: "asset_category_id",
                schema: "pmoc",
                table: "maintenance_plans",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
