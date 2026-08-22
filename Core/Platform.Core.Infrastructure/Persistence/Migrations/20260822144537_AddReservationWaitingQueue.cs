using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReservationWaitingQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "queue_enabled",
                schema: "rentals",
                table: "rental_assets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "queue_opening_time",
                schema: "rentals",
                table: "rental_assets",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "reservation_queue_sessions",
                schema: "rentals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rental_asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    opening_date = table.Column<DateOnly>(type: "date", nullable: false),
                    opens_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    waiting_room_opens_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reservation_queue_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_reservation_queue_sessions_rental_assets_rental_asset_id",
                        column: x => x.rental_asset_id,
                        principalSchema: "rentals",
                        principalTable: "rental_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "reservation_queue_tickets",
                schema: "rentals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    queue_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    turn_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    turn_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_reservation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reservation_queue_tickets", x => x.id);
                    table.ForeignKey(
                        name: "fk_reservation_queue_tickets_customers_customer_id",
                        column: x => x.customer_id,
                        principalSchema: "core",
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_reservation_queue_tickets_reservation_queue_sessions_queue_",
                        column: x => x.queue_session_id,
                        principalSchema: "rentals",
                        principalTable: "reservation_queue_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_reservation_queue_tickets_reservations_completed_reservatio",
                        column: x => x.completed_reservation_id,
                        principalSchema: "rentals",
                        principalTable: "reservations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_reservation_queue_sessions_rental_asset_id",
                schema: "rentals",
                table: "reservation_queue_sessions",
                column: "rental_asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_reservation_queue_sessions_tenant_id_rental_asset_id_openin",
                schema: "rentals",
                table: "reservation_queue_sessions",
                columns: new[] { "tenant_id", "rental_asset_id", "opening_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reservation_queue_tickets_completed_reservation_id",
                schema: "rentals",
                table: "reservation_queue_tickets",
                column: "completed_reservation_id");

            migrationBuilder.CreateIndex(
                name: "ix_reservation_queue_tickets_customer_id",
                schema: "rentals",
                table: "reservation_queue_tickets",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_reservation_queue_tickets_queue_session_id_customer_id",
                schema: "rentals",
                table: "reservation_queue_tickets",
                columns: new[] { "queue_session_id", "customer_id" },
                unique: true,
                filter: "status IN ('Waiting', 'Active')");

            migrationBuilder.CreateIndex(
                name: "ix_reservation_queue_tickets_queue_session_id_sequence",
                schema: "rentals",
                table: "reservation_queue_tickets",
                columns: new[] { "queue_session_id", "sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reservation_queue_tickets",
                schema: "rentals");

            migrationBuilder.DropTable(
                name: "reservation_queue_sessions",
                schema: "rentals");

            migrationBuilder.DropColumn(
                name: "queue_enabled",
                schema: "rentals",
                table: "rental_assets");

            migrationBuilder.DropColumn(
                name: "queue_opening_time",
                schema: "rentals",
                table: "rental_assets");
        }
    }
}
