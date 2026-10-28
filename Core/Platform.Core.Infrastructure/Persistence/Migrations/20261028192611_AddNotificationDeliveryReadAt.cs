using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationDeliveryReadAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "read_at",
                schema: "core",
                table: "notification_deliveries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_inapp_inbox",
                schema: "core",
                table: "notification_deliveries",
                columns: new[] { "tenant_id", "recipient_id", "channel", "read_at", "created_at" },
                descending: new[] { false, false, false, false, true },
                filter: "channel = 'InApp'");

            // Deliveries created before the inbox existed were never surfaced to anyone; mark them
            // read so the feature does not light up every historical InApp delivery as unread on deploy.
            migrationBuilder.Sql(
                "UPDATE core.notification_deliveries SET read_at = now() " +
                "WHERE channel = 'InApp' AND read_at IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_notification_deliveries_inapp_inbox",
                schema: "core",
                table: "notification_deliveries");

            migrationBuilder.DropColumn(
                name: "read_at",
                schema: "core",
                table: "notification_deliveries");
        }
    }
}
