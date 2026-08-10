using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Platform.Core.Infrastructure.Persistence;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260810180000_RenameHeadquartersUnitToMatriz")]
    public partial class RenameHeadquartersUnitToMatriz : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE core.units
                SET name = 'Matriz'
                WHERE name = 'Headquarters';
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE core.units
                SET name = 'Headquarters'
                WHERE name = 'Matriz' AND code = 'HQ';
                """);
        }
    }
}
