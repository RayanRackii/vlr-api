using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkOrderAssignee : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "assigned_user_id",
                schema: "os",
                table: "work_orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_work_orders_assigned_user_id",
                schema: "os",
                table: "work_orders",
                column: "assigned_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_work_orders_tenant_id_assigned_user_id_status",
                schema: "os",
                table: "work_orders",
                columns: new[] { "tenant_id", "assigned_user_id", "status" });

            migrationBuilder.AddForeignKey(
                name: "fk_work_orders_users_assigned_user_id",
                schema: "os",
                table: "work_orders",
                column: "assigned_user_id",
                principalSchema: "core",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_work_orders_users_assigned_user_id",
                schema: "os",
                table: "work_orders");

            migrationBuilder.DropIndex(
                name: "ix_work_orders_assigned_user_id",
                schema: "os",
                table: "work_orders");

            migrationBuilder.DropIndex(
                name: "ix_work_orders_tenant_id_assigned_user_id_status",
                schema: "os",
                table: "work_orders");

            migrationBuilder.DropColumn(
                name: "assigned_user_id",
                schema: "os",
                table: "work_orders");
        }
    }
}
