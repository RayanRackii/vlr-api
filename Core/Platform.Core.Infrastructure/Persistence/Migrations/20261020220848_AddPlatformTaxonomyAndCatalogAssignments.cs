using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformTaxonomyAndCatalogAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO core.permissions (id, key, name, description, module_key, created_at)
                VALUES
                    (gen_random_uuid(), 'core.taxonomy.read', 'Read taxonomy', 'List shared tenant categories and tags.', NULL, now()),
                    (gen_random_uuid(), 'core.taxonomy.manage', 'Manage taxonomy', 'Manage shared tenant categories and tags.', NULL, now())
                ON CONFLICT (key) DO NOTHING;
                """);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_catalog_products_tenant_id_id",
                schema: "catalog",
                table: "catalog_products",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateTable(
                name: "taxonomy_categories",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    slug = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    icon = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_taxonomy_categories", x => x.id);
                    table.UniqueConstraint("ak_taxonomy_categories_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_taxonomy_categories_taxonomy_categories_tenant_id_parent_id",
                        columns: x => new { x.tenant_id, x.parent_id },
                        principalSchema: "core",
                        principalTable: "taxonomy_categories",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_taxonomy_categories_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "core",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "taxonomy_tags",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    slug = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_taxonomy_tags", x => x.id);
                    table.UniqueConstraint("ak_taxonomy_tags_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_taxonomy_tags_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "core",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "catalog_product_categories",
                schema: "catalog",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_product_categories", x => new { x.tenant_id, x.product_id, x.category_id });
                    table.ForeignKey(
                        name: "fk_catalog_product_categories_catalog_products_tenant_id_produ",
                        columns: x => new { x.tenant_id, x.product_id },
                        principalSchema: "catalog",
                        principalTable: "catalog_products",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_catalog_product_categories_taxonomy_categories_tenant_id_ca",
                        columns: x => new { x.tenant_id, x.category_id },
                        principalSchema: "core",
                        principalTable: "taxonomy_categories",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_catalog_product_categories_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "core",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "catalog_product_tags",
                schema: "catalog",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_product_tags", x => new { x.tenant_id, x.product_id, x.tag_id });
                    table.ForeignKey(
                        name: "fk_catalog_product_tags_catalog_products_tenant_id_product_id",
                        columns: x => new { x.tenant_id, x.product_id },
                        principalSchema: "catalog",
                        principalTable: "catalog_products",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_catalog_product_tags_taxonomy_tags_tenant_id_tag_id",
                        columns: x => new { x.tenant_id, x.tag_id },
                        principalSchema: "core",
                        principalTable: "taxonomy_tags",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_catalog_product_tags_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "core",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_catalog_product_categories_tenant_id_category_id_product_id",
                schema: "catalog",
                table: "catalog_product_categories",
                columns: new[] { "tenant_id", "category_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_catalog_product_tags_tenant_id_tag_id_product_id",
                schema: "catalog",
                table: "catalog_product_tags",
                columns: new[] { "tenant_id", "tag_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_taxonomy_categories_tenant_id_parent_id",
                schema: "core",
                table: "taxonomy_categories",
                columns: new[] { "tenant_id", "parent_id" });

            migrationBuilder.CreateIndex(
                name: "ix_taxonomy_categories_tenant_id_slug",
                schema: "core",
                table: "taxonomy_categories",
                columns: new[] { "tenant_id", "slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_taxonomy_tags_tenant_id_normalized_name",
                schema: "core",
                table: "taxonomy_tags",
                columns: new[] { "tenant_id", "normalized_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_taxonomy_tags_tenant_id_slug",
                schema: "core",
                table: "taxonomy_tags",
                columns: new[] { "tenant_id", "slug" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM core.role_permissions WHERE permission_id IN
                    (SELECT id FROM core.permissions WHERE key IN ('core.taxonomy.read', 'core.taxonomy.manage'));
                DELETE FROM core.permissions WHERE key IN ('core.taxonomy.read', 'core.taxonomy.manage');
                """);

            migrationBuilder.DropTable(
                name: "catalog_product_categories",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "catalog_product_tags",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "taxonomy_categories",
                schema: "core");

            migrationBuilder.DropTable(
                name: "taxonomy_tags",
                schema: "core");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_catalog_products_tenant_id_id",
                schema: "catalog",
                table: "catalog_products");
        }
    }
}
