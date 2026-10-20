using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "catalog_policies",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_can_cancel_orders = table.Column<bool>(type: "boolean", nullable: false),
                    order_cancelable_up_to_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    customer_can_cancel_requests = table.Column<bool>(type: "boolean", nullable: false),
                    request_cancelable_up_to_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    cancellation_policy_text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_policies", x => x.id);
                    table.CheckConstraint("ck_catalog_policies_order_status", "order_cancelable_up_to_status IN ('Requested', 'Approved', 'Preparing', 'Ready')");
                    table.CheckConstraint("ck_catalog_policies_request_status", "request_cancelable_up_to_status = 'Submitted'");
                    table.ForeignKey(
                        name: "fk_catalog_policies_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "core",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_catalog_policies_tenant_id",
                schema: "catalog",
                table: "catalog_policies",
                column: "tenant_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "catalog_policies",
                schema: "catalog");
        }
    }
}
