using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssetFamilies : Migration
    {
        private static readonly Guid SpacesId = Guid.Parse("11111111-1111-1111-1111-111111111101");
        private static readonly Guid ElectricalId = Guid.Parse("11111111-1111-1111-1111-111111111102");
        private static readonly Guid GoodsId = Guid.Parse("11111111-1111-1111-1111-111111111103");
        private static readonly Guid GenericId = Guid.Parse("11111111-1111-1111-1111-111111111104");

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "asset_families",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    field_schema_json = table.Column<string>(type: "jsonb", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asset_families", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_asset_families_key",
                schema: "assets",
                table: "asset_families",
                column: "key",
                unique: true);

            migrationBuilder.Sql(
                """
                INSERT INTO assets.asset_families (id, key, label, field_schema_json, sort_order, is_active, created_at, updated_at)
                VALUES
                  ('11111111-1111-1111-1111-111111111101', 'spaces', 'Espaços',
                   '{"fields":[{"key":"capacity","type":"number","required":false,"label":"Capacidade"}]}'::jsonb,
                   10, TRUE, TIMESTAMPTZ '2026-08-06 00:00:00+00', NULL),
                  ('11111111-1111-1111-1111-111111111102', 'electrical', 'Equipamentos elétricos',
                   '{"fields":[{"key":"manufacturer","type":"text","required":false,"label":"Fabricante"},{"key":"voltage","type":"text","required":true,"label":"Voltagem"},{"key":"model","type":"text","required":false,"label":"Modelo"}]}'::jsonb,
                   20, TRUE, TIMESTAMPTZ '2026-08-06 00:00:00+00', NULL),
                  ('11111111-1111-1111-1111-111111111103', 'goods', 'Bens e itens',
                   '{"fields":[{"key":"quantity_hint","type":"number","required":false,"label":"Quantidade típica"}]}'::jsonb,
                   30, TRUE, TIMESTAMPTZ '2026-08-06 00:00:00+00', NULL),
                  ('11111111-1111-1111-1111-111111111104', 'generic', 'Genérico',
                   '{"fields":[]}'::jsonb,
                   100, TRUE, TIMESTAMPTZ '2026-08-06 00:00:00+00', NULL);
                """);

            migrationBuilder.AddColumn<string>(
                name: "attributes",
                schema: "assets",
                table: "assets",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.AddColumn<Guid>(
                name: "family_id",
                schema: "assets",
                table: "assets",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql(
                $"""
                UPDATE assets.assets
                SET family_id = '{GenericId}'
                WHERE family_id IS NULL;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "family_id",
                schema: "assets",
                table: "assets",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "tenant_asset_families",
                schema: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_asset_families", x => x.id);
                    table.ForeignKey(
                        name: "fk_tenant_asset_families_asset_families_family_id",
                        column: x => x.family_id,
                        principalSchema: "assets",
                        principalTable: "asset_families",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tenant_asset_families_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "core",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_assets_family_id",
                schema: "assets",
                table: "assets",
                column: "family_id");

            migrationBuilder.CreateIndex(
                name: "ix_assets_tenant_id_family_id",
                schema: "assets",
                table: "assets",
                columns: new[] { "tenant_id", "family_id" });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_asset_families_family_id",
                schema: "assets",
                table: "tenant_asset_families",
                column: "family_id");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_asset_families_tenant_id_family_id",
                schema: "assets",
                table: "tenant_asset_families",
                columns: new[] { "tenant_id", "family_id" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_assets_asset_families_family_id",
                schema: "assets",
                table: "assets",
                column: "family_id",
                principalSchema: "assets",
                principalTable: "asset_families",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Existing tenants get generic so legacy assets remain editable.
            migrationBuilder.Sql(
                $"""
                INSERT INTO assets.tenant_asset_families (id, tenant_id, family_id, created_at, updated_at)
                SELECT gen_random_uuid(), t.id, '{GenericId}', TIMESTAMPTZ 'now', NULL
                FROM core.tenants t
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM assets.tenant_asset_families taf
                    WHERE taf.tenant_id = t.id AND taf.family_id = '{GenericId}'
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_assets_asset_families_family_id",
                schema: "assets",
                table: "assets");

            migrationBuilder.DropTable(
                name: "tenant_asset_families",
                schema: "assets");

            migrationBuilder.DropTable(
                name: "asset_families",
                schema: "assets");

            migrationBuilder.DropIndex(
                name: "ix_assets_family_id",
                schema: "assets",
                table: "assets");

            migrationBuilder.DropIndex(
                name: "ix_assets_tenant_id_family_id",
                schema: "assets",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "attributes",
                schema: "assets",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "family_id",
                schema: "assets",
                table: "assets");
        }
    }
}
