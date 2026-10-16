using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingHorizon : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "max_advance_booking_days",
                schema: "rentals",
                table: "rental_assets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "max_advance_booking_days",
                schema: "rentals",
                table: "booking_policies",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_rental_assets_advance_days",
                schema: "rentals",
                table: "rental_assets",
                sql: "max_advance_booking_days IS NULL OR (max_advance_booking_days >= 0 AND max_advance_booking_days <= 3650)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_booking_policies_advance_days",
                schema: "rentals",
                table: "booking_policies",
                sql: "max_advance_booking_days IS NULL OR (max_advance_booking_days >= 0 AND max_advance_booking_days <= 3650)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_rental_assets_advance_days",
                schema: "rentals",
                table: "rental_assets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_booking_policies_advance_days",
                schema: "rentals",
                table: "booking_policies");

            migrationBuilder.DropColumn(
                name: "max_advance_booking_days",
                schema: "rentals",
                table: "rental_assets");

            migrationBuilder.DropColumn(
                name: "max_advance_booking_days",
                schema: "rentals",
                table: "booking_policies");
        }
    }
}
