using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UnifyAssetRentableFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing standalone rental assets cannot map unit_id → asset_id; clear early rentals data.
            migrationBuilder.Sql("DELETE FROM rentals.reservation_items;");
            migrationBuilder.Sql("DELETE FROM rentals.reservations;");
            migrationBuilder.Sql("DELETE FROM rentals.rental_pricings;");
            migrationBuilder.Sql("DELETE FROM rentals.rental_assets;");

            migrationBuilder.DropForeignKey(
                name: "fk_rental_assets_units_unit_id",
                schema: "rentals",
                table: "rental_assets");

            migrationBuilder.DropIndex(
                name: "ix_rental_assets_tenant_id_unit_id_name",
                schema: "rentals",
                table: "rental_assets");

            migrationBuilder.DropIndex(
                name: "ix_rental_assets_unit_id",
                schema: "rentals",
                table: "rental_assets");

            migrationBuilder.DropColumn(
                name: "name",
                schema: "rentals",
                table: "rental_assets");

            migrationBuilder.RenameColumn(
                name: "unit_id",
                schema: "rentals",
                table: "rental_assets",
                newName: "asset_id");

            migrationBuilder.AddColumn<bool>(
                name: "is_rentable",
                schema: "assets",
                table: "assets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "requires_maintenance",
                schema: "assets",
                table: "assets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_rental_assets_asset_id",
                schema: "rentals",
                table: "rental_assets",
                column: "asset_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_assets_tenant_id_is_rentable",
                schema: "assets",
                table: "assets",
                columns: new[] { "tenant_id", "is_rentable" });

            migrationBuilder.AddForeignKey(
                name: "fk_rental_assets_assets_asset_id",
                schema: "rentals",
                table: "rental_assets",
                column: "asset_id",
                principalSchema: "assets",
                principalTable: "assets",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_rental_assets_assets_asset_id",
                schema: "rentals",
                table: "rental_assets");

            migrationBuilder.DropIndex(
                name: "ix_rental_assets_asset_id",
                schema: "rentals",
                table: "rental_assets");

            migrationBuilder.DropIndex(
                name: "ix_assets_tenant_id_is_rentable",
                schema: "assets",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "is_rentable",
                schema: "assets",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "requires_maintenance",
                schema: "assets",
                table: "assets");

            migrationBuilder.RenameColumn(
                name: "asset_id",
                schema: "rentals",
                table: "rental_assets",
                newName: "unit_id");

            migrationBuilder.AddColumn<string>(
                name: "name",
                schema: "rentals",
                table: "rental_assets",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "ix_rental_assets_tenant_id_unit_id_name",
                schema: "rentals",
                table: "rental_assets",
                columns: new[] { "tenant_id", "unit_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_rental_assets_unit_id",
                schema: "rentals",
                table: "rental_assets",
                column: "unit_id");

            migrationBuilder.AddForeignKey(
                name: "fk_rental_assets_units_unit_id",
                schema: "rentals",
                table: "rental_assets",
                column: "unit_id",
                principalSchema: "core",
                principalTable: "units",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
