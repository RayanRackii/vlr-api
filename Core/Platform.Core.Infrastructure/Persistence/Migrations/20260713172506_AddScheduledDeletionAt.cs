using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledDeletionAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_assets_asset_categories_category_id",
                schema: "assets",
                table: "assets");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "scheduled_deletion_at",
                schema: "assets",
                table: "assets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "scheduled_deletion_at",
                schema: "assets",
                table: "asset_categories",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_assets_scheduled_deletion_at",
                schema: "assets",
                table: "assets",
                column: "scheduled_deletion_at");

            migrationBuilder.CreateIndex(
                name: "ix_asset_categories_scheduled_deletion_at",
                schema: "assets",
                table: "asset_categories",
                column: "scheduled_deletion_at");

            migrationBuilder.AddForeignKey(
                name: "fk_assets_asset_categories_category_id",
                schema: "assets",
                table: "assets",
                column: "category_id",
                principalSchema: "assets",
                principalTable: "asset_categories",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_assets_asset_categories_category_id",
                schema: "assets",
                table: "assets");

            migrationBuilder.DropIndex(
                name: "ix_assets_scheduled_deletion_at",
                schema: "assets",
                table: "assets");

            migrationBuilder.DropIndex(
                name: "ix_asset_categories_scheduled_deletion_at",
                schema: "assets",
                table: "asset_categories");

            migrationBuilder.DropColumn(
                name: "scheduled_deletion_at",
                schema: "assets",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "scheduled_deletion_at",
                schema: "assets",
                table: "asset_categories");

            migrationBuilder.AddForeignKey(
                name: "fk_assets_asset_categories_category_id",
                schema: "assets",
                table: "assets",
                column: "category_id",
                principalSchema: "assets",
                principalTable: "asset_categories",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
