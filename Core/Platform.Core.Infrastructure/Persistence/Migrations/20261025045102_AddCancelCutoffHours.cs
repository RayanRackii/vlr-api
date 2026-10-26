using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCancelCutoffHours : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "cancel_cutoff_hours",
                schema: "rentals",
                table: "rental_assets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "cancel_cutoff_hours",
                schema: "rentals",
                table: "booking_policies",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cancel_cutoff_hours",
                schema: "rentals",
                table: "rental_assets");

            migrationBuilder.DropColumn(
                name: "cancel_cutoff_hours",
                schema: "rentals",
                table: "booking_policies");
        }
    }
}
