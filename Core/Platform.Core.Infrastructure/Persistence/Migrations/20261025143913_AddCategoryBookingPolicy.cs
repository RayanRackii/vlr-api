using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCategoryBookingPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_booking_policies_tenant_default",
                schema: "rentals",
                table: "booking_policies");

            migrationBuilder.AddColumn<Guid>(
                name: "asset_category_id",
                schema: "rentals",
                table: "booking_policies",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_booking_policies_asset_category_id",
                schema: "rentals",
                table: "booking_policies",
                column: "asset_category_id");

            migrationBuilder.CreateIndex(
                name: "ux_booking_policies_tenant_category",
                schema: "rentals",
                table: "booking_policies",
                columns: new[] { "tenant_id", "asset_category_id" },
                unique: true,
                filter: "asset_category_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_booking_policies_tenant_default",
                schema: "rentals",
                table: "booking_policies",
                column: "tenant_id",
                unique: true,
                filter: "resource_type IS NULL AND asset_category_id IS NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_booking_policies_asset_categories_asset_category_id",
                schema: "rentals",
                table: "booking_policies",
                column: "asset_category_id",
                principalSchema: "assets",
                principalTable: "asset_categories",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_booking_policies_asset_categories_asset_category_id",
                schema: "rentals",
                table: "booking_policies");

            migrationBuilder.DropIndex(
                name: "ix_booking_policies_asset_category_id",
                schema: "rentals",
                table: "booking_policies");

            migrationBuilder.DropIndex(
                name: "ux_booking_policies_tenant_category",
                schema: "rentals",
                table: "booking_policies");

            migrationBuilder.DropIndex(
                name: "ux_booking_policies_tenant_default",
                schema: "rentals",
                table: "booking_policies");

            migrationBuilder.DropColumn(
                name: "asset_category_id",
                schema: "rentals",
                table: "booking_policies");

            migrationBuilder.CreateIndex(
                name: "ux_booking_policies_tenant_default",
                schema: "rentals",
                table: "booking_policies",
                column: "tenant_id",
                unique: true,
                filter: "resource_type IS NULL");
        }
    }
}
