using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LinkReservationToCustomer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "customer_id",
                schema: "rentals",
                table: "reservations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "ix_reservations_customer_id",
                schema: "rentals",
                table: "reservations",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_reservations_tenant_id_customer_id",
                schema: "rentals",
                table: "reservations",
                columns: new[] { "tenant_id", "customer_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_reservations_customers_customer_id",
                schema: "rentals",
                table: "reservations",
                column: "customer_id",
                principalSchema: "core",
                principalTable: "customers",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_reservations_customers_customer_id",
                schema: "rentals",
                table: "reservations");

            migrationBuilder.DropIndex(
                name: "ix_reservations_customer_id",
                schema: "rentals",
                table: "reservations");

            migrationBuilder.DropIndex(
                name: "ix_reservations_tenant_id_customer_id",
                schema: "rentals",
                table: "reservations");

            migrationBuilder.DropColumn(
                name: "customer_id",
                schema: "rentals",
                table: "reservations");
        }
    }
}
