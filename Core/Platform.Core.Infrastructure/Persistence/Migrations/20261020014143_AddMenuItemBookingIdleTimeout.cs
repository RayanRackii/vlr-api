using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMenuItemBookingIdleTimeout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "booking_idle_redirect_path",
                schema: "core",
                table: "tenant_module_menu_items",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "booking_idle_timeout_minutes",
                schema: "core",
                table: "tenant_module_menu_items",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "booking_idle_redirect_path",
                schema: "core",
                table: "tenant_module_menu_items");

            migrationBuilder.DropColumn(
                name: "booking_idle_timeout_minutes",
                schema: "core",
                table: "tenant_module_menu_items");
        }
    }
}
