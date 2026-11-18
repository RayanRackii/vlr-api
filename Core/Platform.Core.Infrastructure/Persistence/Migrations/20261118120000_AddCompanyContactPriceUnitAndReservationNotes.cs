using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations;

/// <summary>
/// Adds the company profile contact and address columns (tenants), the optional catalog product
/// price unit and the internal reservation note. Additive and data-safe: every column is nullable,
/// there is no backfill and nothing is dropped or rewritten. The only data statement is the
/// idempotent insert of the new <c>core.company.write</c> permission row (and its grant to the
/// system Admin/SuperAdmin roles), following AddUnitWriteAndVisualTemplateDefaults.
/// </summary>
public partial class AddCompanyContactPriceUnitAndReservationNotes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "contact_email",
            schema: "core",
            table: "tenants",
            type: "character varying(254)",
            maxLength: 254,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "contact_phone",
            schema: "core",
            table: "tenants",
            type: "character varying(20)",
            maxLength: 20,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "address_postal_code",
            schema: "core",
            table: "tenants",
            type: "character varying(8)",
            maxLength: 8,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "address_street",
            schema: "core",
            table: "tenants",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "address_number",
            schema: "core",
            table: "tenants",
            type: "character varying(20)",
            maxLength: 20,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "address_complement",
            schema: "core",
            table: "tenants",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "address_district",
            schema: "core",
            table: "tenants",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "address_city",
            schema: "core",
            table: "tenants",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "address_state",
            schema: "core",
            table: "tenants",
            type: "character varying(2)",
            maxLength: 2,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "price_unit",
            schema: "catalog",
            table: "catalog_products",
            type: "character varying(16)",
            maxLength: 16,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "notes",
            schema: "rentals",
            table: "reservations",
            type: "character varying(1000)",
            maxLength: 1000,
            nullable: true);

        migrationBuilder.Sql("""
            INSERT INTO core.permissions (id, key, name, description, module_key, created_at)
            VALUES (gen_random_uuid(), 'core.company.write', 'Write company profile', 'Update the company profile (legal data, contact, address).', NULL, now())
            ON CONFLICT (key) DO NOTHING;

            INSERT INTO core.role_permissions (role_id, permission_id, granted_at)
            SELECT r.id, p.id, now()
            FROM core.roles r CROSS JOIN core.permissions p
            WHERE r.is_system_role
              AND lower(r.name) IN ('admin', 'superadmin')
              AND p.key = 'core.company.write'
            ON CONFLICT DO NOTHING;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DELETE FROM core.role_permissions rp
            USING core.permissions p
            WHERE rp.permission_id = p.id
              AND p.key = 'core.company.write';
            DELETE FROM core.permissions WHERE key = 'core.company.write';
            """);

        migrationBuilder.DropColumn(
            name: "contact_email",
            schema: "core",
            table: "tenants");

        migrationBuilder.DropColumn(
            name: "contact_phone",
            schema: "core",
            table: "tenants");

        migrationBuilder.DropColumn(
            name: "address_postal_code",
            schema: "core",
            table: "tenants");

        migrationBuilder.DropColumn(
            name: "address_street",
            schema: "core",
            table: "tenants");

        migrationBuilder.DropColumn(
            name: "address_number",
            schema: "core",
            table: "tenants");

        migrationBuilder.DropColumn(
            name: "address_complement",
            schema: "core",
            table: "tenants");

        migrationBuilder.DropColumn(
            name: "address_district",
            schema: "core",
            table: "tenants");

        migrationBuilder.DropColumn(
            name: "address_city",
            schema: "core",
            table: "tenants");

        migrationBuilder.DropColumn(
            name: "address_state",
            schema: "core",
            table: "tenants");

        migrationBuilder.DropColumn(
            name: "price_unit",
            schema: "catalog",
            table: "catalog_products");

        migrationBuilder.DropColumn(
            name: "notes",
            schema: "rentals",
            table: "reservations");
    }
}
