using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRentalPricingUnit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "pricing_unit",
                schema: "rentals",
                table: "rental_pricings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Hour");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pricing_unit",
                schema: "rentals",
                table: "rental_pricings");
        }
    }
}
