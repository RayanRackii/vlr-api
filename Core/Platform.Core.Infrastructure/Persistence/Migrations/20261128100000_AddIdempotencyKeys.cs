using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations;

/// <summary>
/// Idempotent create endpoints (review P1-B): <c>core.idempotency_keys</c> stores the optional
/// <c>Idempotency-Key</c> a customer sent with an order or reservation create, so a retry returns the
/// original resource instead of creating a duplicate. Additive only: one new table, no existing
/// object is touched. The unique index on (tenant_id, scope, principal_id, key) is what lets exactly
/// one concurrent request claim a key. Rows live 24 hours and the data retention job deletes the
/// expired ones (index on expires_at).
/// </summary>
public partial class AddIdempotencyKeys : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "idempotency_keys",
            schema: "core",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                scope = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                principal_id = table.Column<Guid>(type: "uuid", nullable: false),
                key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                response_status = table.Column<int>(type: "integer", nullable: true),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_idempotency_keys", x => x.id);
                table.ForeignKey(
                    name: "fk_idempotency_keys_tenants_tenant_id",
                    column: x => x.tenant_id,
                    principalSchema: "core",
                    principalTable: "tenants",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ix_idempotency_keys_expires_at",
            schema: "core",
            table: "idempotency_keys",
            column: "expires_at");

        migrationBuilder.CreateIndex(
            name: "ix_idempotency_keys_tenant_id_scope_principal_id_key",
            schema: "core",
            table: "idempotency_keys",
            columns: new[] { "tenant_id", "scope", "principal_id", "key" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The rows are transient replay bookkeeping (24 hours); dropping them loses no business data.
        migrationBuilder.DropTable(
            name: "idempotency_keys",
            schema: "core");
    }
}
