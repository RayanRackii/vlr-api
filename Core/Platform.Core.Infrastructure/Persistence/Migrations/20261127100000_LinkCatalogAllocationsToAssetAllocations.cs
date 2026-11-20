using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations;

/// <summary>
/// Resource planning, slice API-5: links the catalog order allocations to the planning record and
/// lets a requirement target an asset instead of a rental asset. Additive and idempotent.
/// <list type="bullet">
/// <item><description>New nullable columns: <c>asset_allocation_id</c> on
/// <c>catalog.order_item_resource_allocations</c> (tenant-qualified FK to
/// <c>planning.asset_allocations</c>, unique when set) and <c>asset_id</c> on both requirement
/// tables (tenant-qualified FK to <c>assets.assets</c>).</description></item>
/// <item><description><c>reservation_item_id</c> and <c>rental_asset_id</c> of the catalog allocation
/// lose NOT NULL: a planning-only asset has neither.</description></item>
/// <item><description>The "exactly one selector" checks of both requirement tables become "an asset
/// target (asset_id, or a legacy rental_asset_id) or a category target, never both"; a row written
/// by the previous release (rental_asset_id only) stays valid.</description></item>
/// <item><description>Backfill, insert-only apart from the new link columns: <c>asset_id</c> is
/// copied from <c>rentals.rental_assets</c>, and one <c>planning.asset_allocations</c> row
/// (source CatalogOrder, deterministic id <c>md5('catalog-allocation:' || id)::uuid</c>, same status
/// and actuals, work window plus the requirement prep and cleanup, <c>reservation_item_id</c> copied)
/// is inserted per existing catalog allocation and linked. A rerun inserts and links nothing. No row
/// is deleted and no existing column value is rewritten.</description></item>
/// <item><description><c>Down</c> refuses before any step when an allocation has no reservation or
/// rental asset (a planning-only allocation the previous schema cannot hold) or a requirement has no
/// rental asset and no category.</description></item>
/// </list>
/// </summary>
public partial class LinkCatalogAllocationsToAssetAllocations : Migration
{
    private const string OldSelectorCheck =
        "(rental_asset_id IS NOT NULL AND asset_category_id IS NULL) " +
        "OR (rental_asset_id IS NULL AND asset_category_id IS NOT NULL)";

    private const string NewSelectorCheck =
        "(asset_category_id IS NULL AND (asset_id IS NOT NULL OR rental_asset_id IS NOT NULL)) " +
        "OR (asset_category_id IS NOT NULL AND asset_id IS NULL AND rental_asset_id IS NULL)";

    /// <summary>The backfill, public so the SQL test runs the very statements the migration runs.</summary>
    public const string BackfillSql = """
        UPDATE catalog.product_resource_requirements r
        SET asset_id = ra.asset_id
        FROM rentals.rental_assets ra
        WHERE r.rental_asset_id = ra.id
          AND r.tenant_id = ra.tenant_id
          AND r.asset_id IS NULL;

        UPDATE catalog.order_item_resource_requirements r
        SET asset_id = ra.asset_id
        FROM rentals.rental_assets ra
        WHERE r.rental_asset_id = ra.id
          AND r.tenant_id = ra.tenant_id
          AND r.asset_id IS NULL;

        INSERT INTO planning.asset_allocations (
            id, tenant_id, asset_id, unit_id, kind, title, notes, quantity,
            planned_work_start, planned_work_end, occupied_start, occupied_end, time_zone_id,
            status, actual_start_at, actual_work_finished_at, actual_release_at,
            source, catalog_order_item_id, work_order_id, reference_label, reservation_item_id,
            created_by_user_id, actor_id, cancel_reason, row_version, created_at, updated_at)
        SELECT
            md5('catalog-allocation:' || a.id::text)::uuid,
            a.tenant_id,
            ra.asset_id,
            asset.unit_id,
            'Use',
            left('Pedido #' || o.order_number::text || ' - ' || oi.product_name_snapshot, 200),
            NULL,
            GREATEST(a.allocated_quantity, 1),
            a.planned_work_start,
            a.planned_work_end,
            a.planned_work_start - make_interval(mins => GREATEST(req.prep_minutes, 0)),
            a.planned_work_end + make_interval(mins => GREATEST(req.cleanup_minutes, 0)),
            a.time_zone_id,
            a.status,
            a.actual_start_at,
            a.actual_work_finished_at,
            a.actual_release_at,
            'CatalogOrder',
            req.order_item_id,
            NULL,
            'Pedido #' || o.order_number::text,
            a.reservation_item_id,
            NULL,
            a.actor_id,
            a.reason,
            decode(md5('row-version:' || a.id::text), 'hex'),
            a.created_at,
            a.updated_at
        FROM catalog.order_item_resource_allocations a
        JOIN catalog.order_item_resource_requirements req
            ON req.id = a.requirement_id AND req.tenant_id = a.tenant_id
        JOIN catalog.catalog_order_items oi
            ON oi.id = req.order_item_id AND oi.tenant_id = a.tenant_id
        JOIN catalog.catalog_orders o
            ON o.id = oi.order_id AND o.tenant_id = a.tenant_id
        JOIN rentals.rental_assets ra
            ON ra.id = a.rental_asset_id AND ra.tenant_id = a.tenant_id
        JOIN assets.assets asset
            ON asset.id = ra.asset_id AND asset.tenant_id = a.tenant_id
        WHERE a.asset_allocation_id IS NULL
        ON CONFLICT DO NOTHING;

        UPDATE catalog.order_item_resource_allocations a
        SET asset_allocation_id = md5('catalog-allocation:' || a.id::text)::uuid
        WHERE a.asset_allocation_id IS NULL
          AND EXISTS (
              SELECT 1
              FROM planning.asset_allocations p
              WHERE p.id = md5('catalog-allocation:' || a.id::text)::uuid
                AND p.tenant_id = a.tenant_id);

        DO $$
        BEGIN
            IF EXISTS (
                SELECT 1 FROM catalog.order_item_resource_allocations WHERE asset_allocation_id IS NULL) THEN
                RAISE EXCEPTION 'Backfill left catalog order allocations without a planning record; nothing was applied.';
            END IF;
        END $$;
        """;

    /// <summary>The <c>Down</c> guard, public for the same reason as <see cref="BackfillSql"/>.</summary>
    public const string DownGuardSql = """
        DO $$
        BEGIN
            IF EXISTS (
                SELECT 1 FROM catalog.order_item_resource_allocations
                WHERE reservation_item_id IS NULL OR rental_asset_id IS NULL)
               OR EXISTS (
                SELECT 1 FROM catalog.product_resource_requirements
                WHERE rental_asset_id IS NULL AND asset_category_id IS NULL)
               OR EXISTS (
                SELECT 1 FROM catalog.order_item_resource_requirements
                WHERE rental_asset_id IS NULL AND asset_category_id IS NULL) THEN
                RAISE EXCEPTION 'Cannot roll back catalog allocation links while planning-only allocations or requirements exist; recover from a backup or ship a forward migration.';
            END IF;
        END $$;
        """;

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Principal key of the tenant-qualified FK below; id is already the primary key, so the
        // constraint cannot fail on existing data.
        migrationBuilder.AddUniqueConstraint(
            name: "ak_asset_allocations_tenant_id_id",
            schema: "planning",
            table: "asset_allocations",
            columns: new[] { "tenant_id", "id" });

        migrationBuilder.AddColumn<Guid>(
            name: "asset_allocation_id",
            schema: "catalog",
            table: "order_item_resource_allocations",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "asset_id",
            schema: "catalog",
            table: "order_item_resource_requirements",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "asset_id",
            schema: "catalog",
            table: "product_resource_requirements",
            type: "uuid",
            nullable: true);

        migrationBuilder.AlterColumn<Guid>(
            name: "reservation_item_id",
            schema: "catalog",
            table: "order_item_resource_allocations",
            type: "uuid",
            nullable: true,
            oldClrType: typeof(Guid),
            oldType: "uuid");

        migrationBuilder.AlterColumn<Guid>(
            name: "rental_asset_id",
            schema: "catalog",
            table: "order_item_resource_allocations",
            type: "uuid",
            nullable: true,
            oldClrType: typeof(Guid),
            oldType: "uuid");

        migrationBuilder.DropCheckConstraint(
            name: "ck_product_resource_requirements_exactly_one_selector",
            schema: "catalog",
            table: "product_resource_requirements");

        migrationBuilder.AddCheckConstraint(
            name: "ck_product_resource_requirements_exactly_one_selector",
            schema: "catalog",
            table: "product_resource_requirements",
            sql: NewSelectorCheck);

        migrationBuilder.DropCheckConstraint(
            name: "ck_order_item_resource_requirements_exactly_one_selector",
            schema: "catalog",
            table: "order_item_resource_requirements");

        migrationBuilder.AddCheckConstraint(
            name: "ck_order_item_resource_requirements_exactly_one_selector",
            schema: "catalog",
            table: "order_item_resource_requirements",
            sql: NewSelectorCheck);

        migrationBuilder.CreateIndex(
            name: "ix_product_resource_requirements_tenant_id_asset_id",
            schema: "catalog",
            table: "product_resource_requirements",
            columns: new[] { "tenant_id", "asset_id" });

        migrationBuilder.CreateIndex(
            name: "ix_order_item_resource_requirements_tenant_id_asset_id",
            schema: "catalog",
            table: "order_item_resource_requirements",
            columns: new[] { "tenant_id", "asset_id" });

        migrationBuilder.CreateIndex(
            name: "ux_order_item_resource_allocations_tenant_asset_allocation",
            schema: "catalog",
            table: "order_item_resource_allocations",
            columns: new[] { "tenant_id", "asset_allocation_id" },
            unique: true,
            filter: "asset_allocation_id IS NOT NULL");

        migrationBuilder.AddForeignKey(
            name: "fk_product_resource_requirements_assets_tenant_id_asset_id",
            schema: "catalog",
            table: "product_resource_requirements",
            columns: new[] { "tenant_id", "asset_id" },
            principalSchema: "assets",
            principalTable: "assets",
            principalColumns: new[] { "tenant_id", "id" },
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "fk_order_item_resource_requirements_assets_tenant_id_asset_id",
            schema: "catalog",
            table: "order_item_resource_requirements",
            columns: new[] { "tenant_id", "asset_id" },
            principalSchema: "assets",
            principalTable: "assets",
            principalColumns: new[] { "tenant_id", "id" },
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "fk_order_item_resource_allocations_asset_allocations_tenant_id",
            schema: "catalog",
            table: "order_item_resource_allocations",
            columns: new[] { "tenant_id", "asset_allocation_id" },
            principalSchema: "planning",
            principalTable: "asset_allocations",
            principalColumns: new[] { "tenant_id", "id" },
            onDelete: ReferentialAction.Restrict);

        // asset_id from the rental asset, then one planning record per existing catalog allocation.
        // The id derives from the catalog allocation id, so a rerun (or a partial earlier run)
        // finds the row and inserts nothing; ON CONFLICT DO NOTHING also covers the unique
        // reservation-item index. The final check fails the migration loudly rather than leave a
        // catalog allocation unlinked.
        migrationBuilder.Sql(BackfillSql);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Refuse before any step when the previous schema cannot hold the data: an allocation with
        // no reservation or rental asset (a planning-only allocation) or a requirement that has no
        // rental asset and no category. Rows are never deleted or rewritten to get around it;
        // recover from a backup or ship a forward migration (docs/runbooks/database-migrations.md).
        // The planning.asset_allocations rows the backfill inserted are history and stay.
        migrationBuilder.Sql(DownGuardSql);

        migrationBuilder.DropForeignKey(
            name: "fk_order_item_resource_allocations_asset_allocations_tenant_id",
            schema: "catalog",
            table: "order_item_resource_allocations");

        migrationBuilder.DropForeignKey(
            name: "fk_order_item_resource_requirements_assets_tenant_id_asset_id",
            schema: "catalog",
            table: "order_item_resource_requirements");

        migrationBuilder.DropForeignKey(
            name: "fk_product_resource_requirements_assets_tenant_id_asset_id",
            schema: "catalog",
            table: "product_resource_requirements");

        migrationBuilder.DropIndex(
            name: "ux_order_item_resource_allocations_tenant_asset_allocation",
            schema: "catalog",
            table: "order_item_resource_allocations");

        migrationBuilder.DropIndex(
            name: "ix_order_item_resource_requirements_tenant_id_asset_id",
            schema: "catalog",
            table: "order_item_resource_requirements");

        migrationBuilder.DropIndex(
            name: "ix_product_resource_requirements_tenant_id_asset_id",
            schema: "catalog",
            table: "product_resource_requirements");

        migrationBuilder.DropCheckConstraint(
            name: "ck_order_item_resource_requirements_exactly_one_selector",
            schema: "catalog",
            table: "order_item_resource_requirements");

        migrationBuilder.AddCheckConstraint(
            name: "ck_order_item_resource_requirements_exactly_one_selector",
            schema: "catalog",
            table: "order_item_resource_requirements",
            sql: OldSelectorCheck);

        migrationBuilder.DropCheckConstraint(
            name: "ck_product_resource_requirements_exactly_one_selector",
            schema: "catalog",
            table: "product_resource_requirements");

        migrationBuilder.AddCheckConstraint(
            name: "ck_product_resource_requirements_exactly_one_selector",
            schema: "catalog",
            table: "product_resource_requirements",
            sql: OldSelectorCheck);

        migrationBuilder.DropColumn(
            name: "asset_allocation_id",
            schema: "catalog",
            table: "order_item_resource_allocations");

        migrationBuilder.DropColumn(
            name: "asset_id",
            schema: "catalog",
            table: "order_item_resource_requirements");

        migrationBuilder.DropColumn(
            name: "asset_id",
            schema: "catalog",
            table: "product_resource_requirements");

        migrationBuilder.AlterColumn<Guid>(
            name: "rental_asset_id",
            schema: "catalog",
            table: "order_item_resource_allocations",
            type: "uuid",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
            oldClrType: typeof(Guid),
            oldType: "uuid",
            oldNullable: true);

        migrationBuilder.AlterColumn<Guid>(
            name: "reservation_item_id",
            schema: "catalog",
            table: "order_item_resource_allocations",
            type: "uuid",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
            oldClrType: typeof(Guid),
            oldType: "uuid",
            oldNullable: true);

        migrationBuilder.DropUniqueConstraint(
            name: "ak_asset_allocations_tenant_id_id",
            schema: "planning",
            table: "asset_allocations");
    }
}
