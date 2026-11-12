using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddResourceTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "resource_type_id",
                schema: "rentals",
                table: "rental_assets",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "resource_types",
                schema: "rentals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    singular = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    plural = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    article = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    seeded = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    bookable = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    deliverable = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    maintenance = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    fields_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_resource_types", x => x.id);
                    table.CheckConstraint("ck_resource_types_article", "article IN ('um', 'uma')");
                    table.ForeignKey(
                        name: "fk_resource_types_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "core",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_rental_assets_resource_type_id",
                schema: "rentals",
                table: "rental_assets",
                column: "resource_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_resource_types_tenant_id_key",
                schema: "rentals",
                table: "resource_types",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.Sql(
                """
                INSERT INTO rentals.resource_types
                    (id, tenant_id, key, name, singular, plural, article, seeded, bookable, deliverable, maintenance, fields_json, created_at)
                SELECT gen_random_uuid(), t.id, s.key, s.name, s.singular, s.plural, s.article,
                       true, s.bookable, s.deliverable, s.maintenance, '[]'::jsonb, now()
                FROM core.tenants t
                CROSS JOIN (VALUES
                    ('quadra', 'Quadra', 'quadra', 'quadras', 'uma', true, false, true),
                    ('cacamba', 'Caçamba', 'caçamba', 'caçambas', 'uma', false, true, true)
                ) AS s(key, name, singular, plural, article, bookable, deliverable, maintenance)
                WHERE NOT EXISTS (
                    SELECT 1 FROM rentals.resource_types rt
                    WHERE rt.tenant_id = t.id AND rt.key = s.key
                );

                UPDATE rentals.rental_assets ra
                SET resource_type_id = rt.id
                FROM assets.assets a
                JOIN assets.asset_categories c ON c.id = a.category_id AND c.tenant_id = a.tenant_id
                JOIN rentals.resource_types rt ON rt.tenant_id = ra.tenant_id
                WHERE a.id = ra.asset_id
                  AND a.tenant_id = ra.tenant_id
                  AND ra.resource_type_id IS NULL
                  AND (
                    (lower(c.name) IN ('quadra', 'quadras') AND rt.key = 'quadra')
                    OR (lower(c.name) IN ('caçamba', 'cacamba', 'caçambas', 'cacambas') AND rt.key = 'cacamba')
                  );
                """);

            migrationBuilder.AddForeignKey(
                name: "fk_rental_assets_resource_types_resource_type_id",
                schema: "rentals",
                table: "rental_assets",
                column: "resource_type_id",
                principalSchema: "rentals",
                principalTable: "resource_types",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_rental_assets_resource_types_resource_type_id",
                schema: "rentals",
                table: "rental_assets");

            migrationBuilder.DropTable(
                name: "resource_types",
                schema: "rentals");

            migrationBuilder.DropIndex(
                name: "ix_rental_assets_resource_type_id",
                schema: "rentals",
                table: "rental_assets");

            migrationBuilder.DropColumn(
                name: "resource_type_id",
                schema: "rentals",
                table: "rental_assets");
        }
    }
}
