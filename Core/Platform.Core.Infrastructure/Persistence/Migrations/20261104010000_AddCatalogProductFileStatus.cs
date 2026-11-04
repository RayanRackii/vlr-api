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


    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // No Up-time marker is persisted: conservatively refuse rollback of any populated
        // table. This includes post-Up private Ready rows as well as Pending/Failed and
        // legacy rows, whose status/provenance would otherwise be lost on Down -> Up.
        // Lock out concurrent inserts until the column drops commit.
        migrationBuilder.Sql("""
            LOCK TABLE catalog.catalog_product_files IN ACCESS EXCLUSIVE MODE;
            DO $rollback$
            BEGIN
                IF EXISTS (SELECT 1 FROM catalog.catalog_product_files) THEN
                    RAISE EXCEPTION 'Catalog file rollback refused: populated table may contain post-Up status or storage provenance.';
                END IF;
            END;
            $rollback$;
            """);
        migrationBuilder.DropColumn(name: "is_legacy_public", schema: "catalog", table: "catalog_product_files");
        migrationBuilder.DropColumn(name: "status", schema: "catalog", table: "catalog_product_files");
    }
}
