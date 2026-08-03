using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantBrandingAndCustomerPortal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "accent_color",
                schema: "core",
                table: "tenants",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "primary_color",
                schema: "core",
                table: "tenants",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "welcome_tagline",
                schema: "core",
                table: "tenants",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "address_city",
                schema: "core",
                table: "customers",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "address_neighborhood",
                schema: "core",
                table: "customers",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "address_state",
                schema: "core",
                table: "customers",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "address_street",
                schema: "core",
                table: "customers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cpf",
                schema: "core",
                table: "customers",
                type: "character varying(11)",
                maxLength: 11,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "password_hash",
                schema: "core",
                table: "customers",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "phone_verified_at",
                schema: "core",
                table: "customers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "photo_url",
                schema: "core",
                table: "customers",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "postal_code",
                schema: "core",
                table: "customers",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_customers_tenant_id_cpf",
                schema: "core",
                table: "customers",
                columns: new[] { "tenant_id", "cpf" },
                unique: true,
                filter: "cpf IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_customers_tenant_id_cpf",
                schema: "core",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "accent_color",
                schema: "core",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "primary_color",
                schema: "core",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "welcome_tagline",
                schema: "core",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "address_city",
                schema: "core",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "address_neighborhood",
                schema: "core",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "address_state",
                schema: "core",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "address_street",
                schema: "core",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "cpf",
                schema: "core",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "password_hash",
                schema: "core",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "phone_verified_at",
                schema: "core",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "photo_url",
                schema: "core",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "postal_code",
                schema: "core",
                table: "customers");
        }
    }
}
