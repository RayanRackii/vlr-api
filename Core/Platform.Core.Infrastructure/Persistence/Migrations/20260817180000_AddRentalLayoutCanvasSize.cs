using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Platform.Core.Infrastructure.Persistence;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260817180000_AddRentalLayoutCanvasSize")]
    public partial class AddRentalLayoutCanvasSize : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "aspect_ratio",
                schema: "rentals",
                table: "layouts",
                type: "double precision",
                nullable: false,
                defaultValue: 1.6);

            migrationBuilder.AddColumn<double>(
                name: "width_percent",
                schema: "rentals",
                table: "layouts",
                type: "double precision",
                nullable: false,
                defaultValue: 100.0);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "aspect_ratio",
                schema: "rentals",
                table: "layouts");

            migrationBuilder.DropColumn(
                name: "width_percent",
                schema: "rentals",
                table: "layouts");
        }
    }
}
