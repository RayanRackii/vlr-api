using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerGroupsAndProductVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "visibility",
                schema: "catalog",
                table: "catalog_products",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Public");

            migrationBuilder.AddUniqueConstraint(
                name: "ak_customers_tenant_id_id",
                schema: "core",
                table: "customers",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateTable(
                name: "catalog_access_audit",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    before_json = table.Column<string>(type: "jsonb", nullable: true),
                    after_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_access_audit", x => x.id);
                    table.ForeignKey(
                        name: "fk_catalog_access_audit_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "core",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "customer_groups",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_groups", x => x.id);
                    table.UniqueConstraint("ak_customer_groups_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_customer_groups_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "core",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "catalog_product_access",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_product_access", x => x.id);
                    table.CheckConstraint("ck_catalog_product_access_exactly_one_target", "(customer_id IS NOT NULL AND group_id IS NULL) OR (customer_id IS NULL AND group_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_catalog_product_access_catalog_products_tenant_id_product_id",
                        columns: x => new { x.tenant_id, x.product_id },
                        principalSchema: "catalog",
                        principalTable: "catalog_products",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_catalog_product_access_customer_groups_tenant_id_group_id",
                        columns: x => new { x.tenant_id, x.group_id },
                        principalSchema: "catalog",
                        principalTable: "customer_groups",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_catalog_product_access_customers_tenant_id_customer_id",
                        columns: x => new { x.tenant_id, x.customer_id },
                        principalSchema: "core",
                        principalTable: "customers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "customer_group_members",
                schema: "catalog",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_group_members", x => new { x.tenant_id, x.group_id, x.customer_id });
                    table.ForeignKey(
                        name: "fk_customer_group_members_customer_groups_tenant_id_group_id",
                        columns: x => new { x.tenant_id, x.group_id },
                        principalSchema: "catalog",
                        principalTable: "customer_groups",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_customer_group_members_customers_tenant_id_customer_id",
                        columns: x => new { x.tenant_id, x.customer_id },
                        principalSchema: "core",
                        principalTable: "customers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_customer_group_members_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "core",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_catalog_access_audit_tenant_id_entity_type_entity_id_create",
                schema: "catalog",
                table: "catalog_access_audit",
                columns: new[] { "tenant_id", "entity_type", "entity_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_catalog_product_access_tenant_id_customer_id",
                schema: "catalog",
                table: "catalog_product_access",
                columns: new[] { "tenant_id", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "ix_catalog_product_access_tenant_id_group_id",
                schema: "catalog",
                table: "catalog_product_access",
                columns: new[] { "tenant_id", "group_id" });

            migrationBuilder.CreateIndex(
                name: "ix_catalog_product_access_tenant_id_product_id_customer_id",
                schema: "catalog",
                table: "catalog_product_access",
                columns: new[] { "tenant_id", "product_id", "customer_id" },
                unique: true,
                filter: "customer_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_product_access_tenant_id_product_id_group_id",
                schema: "catalog",
                table: "catalog_product_access",
                columns: new[] { "tenant_id", "product_id", "group_id" },
                unique: true,
                filter: "group_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_customer_group_members_tenant_id_customer_id",
                schema: "catalog",
                table: "customer_group_members",
                columns: new[] { "tenant_id", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "ix_customer_groups_tenant_id_name",
                schema: "catalog",
                table: "customer_groups",
                columns: new[] { "tenant_id", "name" });

            // Case-insensitive group-name uniqueness per tenant (expression index, not expressible in the EF model).
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ux_customer_groups_tenant_lower_name ON catalog.customer_groups (tenant_id, lower(name));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS catalog.ux_customer_groups_tenant_lower_name;");

            migrationBuilder.DropTable(
                name: "catalog_access_audit",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "catalog_product_access",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "customer_group_members",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "customer_groups",
                schema: "catalog");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_customers_tenant_id_id",
                schema: "core",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "visibility",
                schema: "catalog",
                table: "catalog_products");
        }
    }
}
