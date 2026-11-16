using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations;

/// <summary>
/// Adds the per-tenant sequential work order number ("OS #n"). Additive and data-safe:
/// the column is created nullable, backfilled, the counter table is seeded, and only then
/// is the column made NOT NULL and the unique index created. Nothing is deleted or rewritten
/// apart from filling the new column.
/// </summary>
public partial class AddWorkOrderNumber : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "work_order_number_sequences",
            schema: "os",
            columns: table => new
            {
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                last_number = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_work_order_number_sequences", x => x.tenant_id);
            });

        migrationBuilder.AddColumn<int>(
            name: "number",
            schema: "os",
            table: "work_orders",
            type: "integer",
            nullable: true);

        // Backfill: 1..n per tenant in creation order (id breaks ties deterministically).
        migrationBuilder.Sql(
            """
            UPDATE os.work_orders AS w
            SET number = r.rn
            FROM (
                SELECT id,
                       row_number() OVER (PARTITION BY tenant_id ORDER BY created_at, id) AS rn
                FROM os.work_orders
            ) AS r
            WHERE w.id = r.id;
            """);

        // Seed each tenant's counter with its highest assigned number.
        migrationBuilder.Sql(
            """
            INSERT INTO os.work_order_number_sequences (tenant_id, last_number)
            SELECT tenant_id, max(number)
            FROM os.work_orders
            GROUP BY tenant_id
            ON CONFLICT (tenant_id) DO UPDATE
                SET last_number = GREATEST(os.work_order_number_sequences.last_number, EXCLUDED.last_number);
            """);

        migrationBuilder.AlterColumn<int>(
            name: "number",
            schema: "os",
            table: "work_orders",
            type: "integer",
            nullable: false,
            oldClrType: typeof(int),
            oldType: "integer",
            oldNullable: true);

        migrationBuilder.CreateIndex(
            name: "ix_work_orders_tenant_id_number",
            schema: "os",
            table: "work_orders",
            columns: new[] { "tenant_id", "number" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_work_orders_tenant_id_number",
            schema: "os",
            table: "work_orders");

        migrationBuilder.DropTable(
            name: "work_order_number_sequences",
            schema: "os");

        migrationBuilder.DropColumn(
            name: "number",
            schema: "os",
            table: "work_orders");
    }
}
