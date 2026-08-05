using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UserMembershipPerTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_users_supabase_auth_id",
                schema: "core",
                table: "users");

            migrationBuilder.CreateIndex(
                name: "ix_users_tenant_id_supabase_auth_id",
                schema: "core",
                table: "users",
                columns: new[] { "tenant_id", "supabase_auth_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_users_tenant_id_supabase_auth_id",
                schema: "core",
                table: "users");

            migrationBuilder.CreateIndex(
                name: "ix_users_supabase_auth_id",
                schema: "core",
                table: "users",
                column: "supabase_auth_id",
                unique: true);
        }
    }
}
