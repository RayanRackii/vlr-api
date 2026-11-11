using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingLeadTimeAndDepositPercent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "deposit_percent",
                schema: "rentals",
                table: "rental_assets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "minimum_booking_lead_minutes",
                schema: "rentals",
                table: "rental_assets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "deposit_percent",
                schema: "rentals",
                table: "booking_policies",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "minimum_booking_lead_minutes",
                schema: "rentals",
                table: "booking_policies",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_rental_assets_deposit_percent",
                schema: "rentals",
                table: "rental_assets",
                sql: "deposit_percent IS NULL OR (deposit_percent >= 0 AND deposit_percent <= 100)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_rental_assets_lead_minutes",
                schema: "rentals",
                table: "rental_assets",
                sql: "minimum_booking_lead_minutes IS NULL OR (minimum_booking_lead_minutes >= 0 AND minimum_booking_lead_minutes <= 525600)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_booking_policies_deposit_percent",
                schema: "rentals",
                table: "booking_policies",
                sql: "deposit_percent IS NULL OR (deposit_percent >= 0 AND deposit_percent <= 100)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_booking_policies_lead_minutes",
                schema: "rentals",
                table: "booking_policies",
                sql: "minimum_booking_lead_minutes IS NULL OR (minimum_booking_lead_minutes >= 0 AND minimum_booking_lead_minutes <= 525600)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_rental_assets_deposit_percent",
                schema: "rentals",
                table: "rental_assets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_rental_assets_lead_minutes",
                schema: "rentals",
                table: "rental_assets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_booking_policies_deposit_percent",
                schema: "rentals",
                table: "booking_policies");

            migrationBuilder.DropCheckConstraint(
                name: "ck_booking_policies_lead_minutes",
                schema: "rentals",
                table: "booking_policies");

            migrationBuilder.DropColumn(
                name: "deposit_percent",
                schema: "rentals",
                table: "rental_assets");

            migrationBuilder.DropColumn(
                name: "minimum_booking_lead_minutes",
                schema: "rentals",
                table: "rental_assets");

            migrationBuilder.DropColumn(
                name: "deposit_percent",
                schema: "rentals",
                table: "booking_policies");

            migrationBuilder.DropColumn(
                name: "minimum_booking_lead_minutes",
                schema: "rentals",
                table: "booking_policies");
        }
    }
}
