using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations;

/// <summary>
/// Adds the customer portal landing appearance columns to core.tenants (hero image key and three
/// texts). Additive and data-safe: every column is nullable, no backfill, nothing dropped.
/// </summary>
public partial class AddTenantPortalLanding : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "landing_image_key",
            schema: "core",
            table: "tenants",
            type: "character varying(300)",
            maxLength: 300,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "landing_kicker",
            schema: "core",
            table: "tenants",
            type: "character varying(60)",
            maxLength: 60,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "landing_headline",
            schema: "core",
            table: "tenants",
            type: "character varying(120)",
            maxLength: 120,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "landing_subtitle",
            schema: "core",
            table: "tenants",
            type: "character varying(240)",
            maxLength: 240,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "landing_image_key",
            schema: "core",
            table: "tenants");

        migrationBuilder.DropColumn(
            name: "landing_kicker",
            schema: "core",
            table: "tenants");

        migrationBuilder.DropColumn(
            name: "landing_headline",
            schema: "core",
            table: "tenants");

        migrationBuilder.DropColumn(
            name: "landing_subtitle",
            schema: "core",
            table: "tenants");
    }
}
