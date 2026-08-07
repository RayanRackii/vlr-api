using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Platform.Core.Infrastructure.Persistence;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20260806193000_AddTenantTrialAndSignupClaims")]
    public partial class AddTenantTrialAndSignupClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_trial",
                schema: "core",
                table: "tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "trial_ends_at",
                schema: "core",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "trial_purge_at",
                schema: "core",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "notifications_email_only",
                schema: "core",
                table: "tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "trial_signup_claims",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email_normalized = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    phone_normalized = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trial_signup_claims", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_trial_signup_claims_email_normalized",
                schema: "core",
                table: "trial_signup_claims",
                column: "email_normalized",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_trial_signup_claims_phone_normalized",
                schema: "core",
                table: "trial_signup_claims",
                column: "phone_normalized",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "trial_signup_claims",
                schema: "core");

            migrationBuilder.DropColumn(
                name: "is_trial",
                schema: "core",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "trial_ends_at",
                schema: "core",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "trial_purge_at",
                schema: "core",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "notifications_email_only",
                schema: "core",
                table: "tenants");
        }
    }
}
