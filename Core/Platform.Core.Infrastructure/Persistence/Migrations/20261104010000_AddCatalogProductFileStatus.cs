using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations;

public partial class AddCatalogProductFileStatus : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "status",
            schema: "catalog",
            table: "catalog_product_files",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "Ready");

        migrationBuilder.AddColumn<bool>(
            name: "is_legacy_public",
            schema: "catalog",
            table: "catalog_product_files",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        // Existing objects retain their original storage location; all future files are private.
        migrationBuilder.Sql("""
            UPDATE catalog.catalog_product_files AS f
            SET is_legacy_public = TRUE
            FROM catalog.catalog_products AS p
            WHERE f.product_id = p.id AND f.tenant_id = p.tenant_id
              AND f.visibility = 'CustomerVisible' AND p.visibility = 'Public';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "is_legacy_public", schema: "catalog", table: "catalog_product_files");
        migrationBuilder.DropColumn(name: "status", schema: "catalog", table: "catalog_product_files");
    }
}
