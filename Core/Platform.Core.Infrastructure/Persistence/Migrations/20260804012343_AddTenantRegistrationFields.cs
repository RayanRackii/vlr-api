using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantRegistrationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Dictionary<string, string>>(
                name: "extra_attributes",
                schema: "core",
                table: "customers",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.CreateTable(
                name: "tenant_registration_fields",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    field_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    is_required = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    options_json = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_registration_fields", x => x.id);
                    table.ForeignKey(
                        name: "fk_tenant_registration_fields_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "core",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_customers_tenant_id_name",
                schema: "core",
                table: "customers",
                columns: new[] { "tenant_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_registration_fields_tenant_id_field_key",
                schema: "core",
                table: "tenant_registration_fields",
                columns: new[] { "tenant_id", "field_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenant_registration_fields_tenant_id_sort_order",
                schema: "core",
                table: "tenant_registration_fields",
                columns: new[] { "tenant_id", "sort_order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_registration_fields",
                schema: "core");

            migrationBuilder.DropIndex(
                name: "ix_customers_tenant_id_name",
                schema: "core",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "extra_attributes",
                schema: "core",
                table: "customers");
        }
    }
}
