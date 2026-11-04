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
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "status", schema: "catalog", table: "catalog_product_files");
    }
}
