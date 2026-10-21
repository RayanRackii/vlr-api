using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations;

public partial class SeedResourceVisualCatalogV1 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(Seed.ResourceVisualCatalogV1Sql.Up());

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(Seed.ResourceVisualCatalogV1Sql.Down());
}
