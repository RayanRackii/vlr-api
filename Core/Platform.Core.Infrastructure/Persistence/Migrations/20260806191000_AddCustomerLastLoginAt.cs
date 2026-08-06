using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Platform.Core.Infrastructure.Persistence;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20260806191000_AddCustomerLastLoginAt")]
    public partial class AddCustomerLastLoginAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_login_at",
                schema: "core",
                table: "customers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_customers_tenant_id_last_login_at",
                schema: "core",
                table: "customers",
                columns: new[] { "tenant_id", "last_login_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_customers_tenant_id_last_login_at",
                schema: "core",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "last_login_at",
                schema: "core",
                table: "customers");
        }
    }
}
