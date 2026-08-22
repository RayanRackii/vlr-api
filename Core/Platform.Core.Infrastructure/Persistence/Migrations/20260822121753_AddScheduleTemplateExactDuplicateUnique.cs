using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduleTemplateExactDuplicateUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_schedule_templates_exact_duplicate",
                schema: "rentals",
                table: "schedule_templates",
                columns: new[] { "tenant_id", "rental_asset_id", "day_of_week", "start_time", "end_time", "occupancy_kind_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_schedule_templates_exact_duplicate",
                schema: "rentals",
                table: "schedule_templates");
        }
    }
}
