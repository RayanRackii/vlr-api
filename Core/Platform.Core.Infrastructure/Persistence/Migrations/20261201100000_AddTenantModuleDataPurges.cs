using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations;

/// <summary>
/// Module data purge, slice API-3 (spec 2026-11-20-module-data-purge). Additive only: one new table
/// <c>core.tenant_module_data_purges</c> (a scheduled, cancellable removal of a disabled module's data)
/// with a unique partial index that allows one pending purge per tenant and module. No existing table,
/// column or row is touched, so the previous API version keeps working during the rollout.
/// </summary>
public partial class AddTenantModuleDataPurges : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "tenant_module_data_purges",
            schema: "core",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                module = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                requested_by_user_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                scheduled_for = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                cancelled_by_user_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                cancel_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                executed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                result_json = table.Column<string>(type: "jsonb", nullable: true),
                row_version = table.Column<byte[]>(type: "bytea", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_tenant_module_data_purges", x => x.id);
                table.ForeignKey(
                    name: "fk_tenant_module_data_purges_tenants_tenant_id",
                    column: x => x.tenant_id,
                    principalSchema: "core",
                    principalTable: "tenants",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ux_tenant_module_data_purges_pending",
            schema: "core",
            table: "tenant_module_data_purges",
            columns: new[] { "tenant_id", "module" },
            unique: true,
            filter: "cancelled_at IS NULL AND executed_at IS NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Rolling back drops the purge schedule and its audit trail. Prefer a forward migration;
        // see docs/runbooks/database-migrations.md.
        migrationBuilder.DropTable(
            name: "tenant_module_data_purges",
            schema: "core");
    }
}
