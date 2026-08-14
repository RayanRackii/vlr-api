using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Platform.Core.Infrastructure.Persistence;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260814180000_AddOccupancyKindDescriptionAndIcon")]
    public partial class AddOccupancyKindDescriptionAndIcon : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "description",
                schema: "rentals",
                table: "occupancy_kinds",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "icon_key",
                schema: "rentals",
                table: "occupancy_kinds",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "description",
                schema: "rentals",
                table: "occupancy_kinds");

            migrationBuilder.DropColumn(
                name: "icon_key",
                schema: "rentals",
                table: "occupancy_kinds");
        }
    }
}
