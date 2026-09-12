using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class B2cEmailVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "code",
                schema: "core",
                table: "otp_codes",
                type: "character(6)",
                fixedLength: true,
                maxLength: 6,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character(6)",
                oldFixedLength: true,
                oldMaxLength: 6);

            migrationBuilder.AddColumn<int>(
                name: "attempts",
                schema: "core",
                table: "otp_codes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "code_hash",
                schema: "core",
                table: "otp_codes",
                type: "character varying(88)",
                maxLength: 88,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "purpose",
                schema: "core",
                table: "otp_codes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "Legacy");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "replaced_at",
                schema: "core",
                table: "otp_codes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "email_verified_at",
                schema: "core",
                table: "customers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE core.customers SET email_verified_at = phone_verified_at WHERE phone_verified_at IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "attempts",
                schema: "core",
                table: "otp_codes");

            migrationBuilder.DropColumn(
                name: "code_hash",
                schema: "core",
                table: "otp_codes");

            migrationBuilder.DropColumn(
                name: "purpose",
                schema: "core",
                table: "otp_codes");

            migrationBuilder.DropColumn(
                name: "replaced_at",
                schema: "core",
                table: "otp_codes");

            migrationBuilder.DropColumn(
                name: "email_verified_at",
                schema: "core",
                table: "customers");

            migrationBuilder.AlterColumn<string>(
                name: "code",
                schema: "core",
                table: "otp_codes",
                type: "character(6)",
                fixedLength: true,
                maxLength: 6,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character(6)",
                oldFixedLength: true,
                oldMaxLength: 6,
                oldNullable: true);
        }
    }
}
