using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserFullNameAndTenantIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_users_full_name",
                schema: "core",
                table: "users",
                column: "full_name");

            migrationBuilder.CreateIndex(
                name: "ix_users_tenant_id",
                schema: "core",
                table: "users",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_users_full_name",
                schema: "core",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_users_tenant_id",
                schema: "core",
                table: "users");
        }
    }
}
