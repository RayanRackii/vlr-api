using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssetsModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "assets");

            migrationBuilder.CreateTable(
                name: "asset_categories",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    manufacturer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asset_categories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "assets",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    tag = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    location = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    serial_number = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    installation_date = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Active"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assets", x => x.id);
                    table.ForeignKey(
                        name: "fk_assets_asset_categories_category_id",
                        column: x => x.category_id,
                        principalSchema: "assets",
                        principalTable: "asset_categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_assets_units_unit_id",
                        column: x => x.unit_id,
                        principalSchema: "core",
                        principalTable: "units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_asset_categories_tenant_id_name",
                schema: "assets",
                table: "asset_categories",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_assets_category_id",
                schema: "assets",
                table: "assets",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_assets_tenant_id_category_id",
                schema: "assets",
                table: "assets",
                columns: new[] { "tenant_id", "category_id" });

            migrationBuilder.CreateIndex(
                name: "ix_assets_tenant_id_tag",
                schema: "assets",
                table: "assets",
                columns: new[] { "tenant_id", "tag" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_assets_tenant_id_unit_id",
                schema: "assets",
                table: "assets",
                columns: new[] { "tenant_id", "unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_assets_unit_id",
                schema: "assets",
                table: "assets",
                column: "unit_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assets",
                schema: "assets");

            migrationBuilder.DropTable(
                name: "asset_categories",
                schema: "assets");
        }
    }
}
