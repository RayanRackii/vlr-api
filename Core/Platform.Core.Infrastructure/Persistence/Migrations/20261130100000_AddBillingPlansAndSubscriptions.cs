using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations;

/// <summary>
/// Plans, pricing and billing, slice B1 (spec 2026-11-21-plans-pricing-billing). Additive only: a new
/// <c>billing</c> schema with <c>plan_catalog</c>, <c>tenant_subscriptions</c> and
/// <c>subscription_events</c>, two nullable lead columns on <c>core.tenants</c>, the seed of the nine
/// catalog rows (3 packages x 3 plans), a backfill of one current subscription per existing tenant and
/// the <c>core.settings.billing</c> permission with grants to the system Admin and SuperAdmin roles.
/// No existing column or row is changed, so the previous API version keeps working during the rollout.
/// The seed and the backfill are plain SQL constants (<see cref="BillingMigrationSql"/>), idempotent
/// (<c>ON CONFLICT DO NOTHING</c> / <c>WHERE NOT EXISTS</c>), so running them again is a no-op.
/// </summary>
public partial class AddBillingPlansAndSubscriptions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "billing");

        migrationBuilder.AddColumn<string>(
            name: "lead_campaign",
            schema: "core",
            table: "tenants",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "lead_source",
            schema: "core",
            table: "tenants",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "plan_catalog",
            schema: "billing",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                package_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                plan_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                monthly_price_cents = table.Column<int>(type: "integer", nullable: false),
                annual_price_cents = table.Column<int>(type: "integer", nullable: false),
                currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                modules = table.Column<string>(type: "jsonb", nullable: false),
                limits = table.Column<string>(type: "jsonb", nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                sort_order = table.Column<int>(type: "integer", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_plan_catalog", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "tenant_subscriptions",
            schema: "billing",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                plan_catalog_id = table.Column<Guid>(type: "uuid", nullable: false),
                billing_cycle = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                billing_mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "provider"),
                limits_unlimited = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                current_period_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                current_period_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                discount_percent = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                discount_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                provider_customer_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                provider_subscription_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                past_due_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                canceled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                row_version = table.Column<byte[]>(type: "bytea", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_tenant_subscriptions", x => x.id);
                table.ForeignKey(
                    name: "fk_tenant_subscriptions_plan_catalog_plan_catalog_id",
                    column: x => x.plan_catalog_id,
                    principalSchema: "billing",
                    principalTable: "plan_catalog",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_tenant_subscriptions_tenants_tenant_id",
                    column: x => x.tenant_id,
                    principalSchema: "core",
                    principalTable: "tenants",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "subscription_events",
            schema: "billing",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                provider_event_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                payload = table.Column<string>(type: "jsonb", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_subscription_events", x => x.id);
                table.ForeignKey(
                    name: "fk_subscription_events_tenant_subscriptions_subscription_id",
                    column: x => x.subscription_id,
                    principalSchema: "billing",
                    principalTable: "tenant_subscriptions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_subscription_events_tenants_tenant_id",
                    column: x => x.tenant_id,
                    principalSchema: "core",
                    principalTable: "tenants",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ix_plan_catalog_package_key_plan_key",
            schema: "billing",
            table: "plan_catalog",
            columns: new[] { "package_key", "plan_key" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_subscription_events_subscription_id",
            schema: "billing",
            table: "subscription_events",
            column: "subscription_id");

        migrationBuilder.CreateIndex(
            name: "ix_subscription_events_tenant_id_created_at",
            schema: "billing",
            table: "subscription_events",
            columns: new[] { "tenant_id", "created_at" });

        migrationBuilder.CreateIndex(
            name: "ux_subscription_events_provider_event_id",
            schema: "billing",
            table: "subscription_events",
            column: "provider_event_id",
            unique: true,
            filter: "provider_event_id IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_tenant_subscriptions_plan_catalog_id",
            schema: "billing",
            table: "tenant_subscriptions",
            column: "plan_catalog_id");

        migrationBuilder.CreateIndex(
            name: "ux_tenant_subscriptions_provider_subscription",
            schema: "billing",
            table: "tenant_subscriptions",
            column: "provider_subscription_id",
            unique: true,
            filter: "provider_subscription_id IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ux_tenant_subscriptions_tenant_current",
            schema: "billing",
            table: "tenant_subscriptions",
            column: "tenant_id",
            unique: true,
            filter: "ended_at IS NULL");

        migrationBuilder.Sql(BillingMigrationSql.SeedPlanCatalog);
        migrationBuilder.Sql(BillingMigrationSql.BackfillSubscriptions);
        migrationBuilder.Sql(BillingMigrationSql.GrantBillingPermission);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Rolling back drops billing data (subscriptions, history, catalog edits) and the lead columns.
        // Prefer a Supabase backup restore or a forward migration; see docs/runbooks/database-migrations.md.
        // The permission row and its grants stay: they belong to the catalog, like the resource-planning ones.
        migrationBuilder.DropTable(
            name: "subscription_events",
            schema: "billing");

        migrationBuilder.DropTable(
            name: "tenant_subscriptions",
            schema: "billing");

        migrationBuilder.DropTable(
            name: "plan_catalog",
            schema: "billing");

        migrationBuilder.DropColumn(
            name: "lead_campaign",
            schema: "core",
            table: "tenants");

        migrationBuilder.DropColumn(
            name: "lead_source",
            schema: "core",
            table: "tenants");
    }
}

/// <summary>
/// The data steps of <see cref="AddBillingPlansAndSubscriptions"/> as constants so a test can run them
/// again and prove they are idempotent.
/// </summary>
public static class BillingMigrationSql
{
    /// <summary>
    /// Nine catalog rows (D1 prices in cents, annual = 10 x monthly; D2 modules; D3 limits from the pricing
    /// study table 4.3). A null limit is unlimited. Completo of Reservas adds catalog, Completo of
    /// Manutencao adds resource-planning.
    /// </summary>
    public const string SeedPlanCatalog = """
        INSERT INTO billing.plan_catalog
            (id, package_key, plan_key, name, monthly_price_cents, annual_price_cents, currency, modules, limits, is_active, sort_order, created_at)
        VALUES
            (gen_random_uuid(), 'reservas', 'essencial', 'Essencial', 9900, 99000, 'BRL',
             '["rentals"]'::jsonb,
             '{"assets":null,"rentable_resources":3,"users":3,"units":1,"whatsapp_messages_month":0}'::jsonb, true, 11, now()),
            (gen_random_uuid(), 'reservas', 'profissional', 'Profissional', 18900, 189000, 'BRL',
             '["rentals"]'::jsonb,
             '{"assets":null,"rentable_resources":8,"users":10,"units":1,"whatsapp_messages_month":500}'::jsonb, true, 12, now()),
            (gen_random_uuid(), 'reservas', 'completo', 'Completo', 29900, 299000, 'BRL',
             '["rentals","catalog"]'::jsonb,
             '{"assets":null,"rentable_resources":25,"users":30,"units":3,"whatsapp_messages_month":2000}'::jsonb, true, 13, now()),
            (gen_random_uuid(), 'manutencao', 'essencial', 'Essencial', 9900, 99000, 'BRL',
             '["inventory","pmoc","os"]'::jsonb,
             '{"assets":150,"rentable_resources":null,"users":2,"units":1,"whatsapp_messages_month":0}'::jsonb, true, 21, now()),
            (gen_random_uuid(), 'manutencao', 'profissional', 'Profissional', 18900, 189000, 'BRL',
             '["inventory","pmoc","os"]'::jsonb,
             '{"assets":800,"rentable_resources":null,"users":6,"units":1,"whatsapp_messages_month":500}'::jsonb, true, 22, now()),
            (gen_random_uuid(), 'manutencao', 'completo', 'Completo', 29900, 299000, 'BRL',
             '["inventory","pmoc","os","resource-planning"]'::jsonb,
             '{"assets":3000,"rentable_resources":null,"users":15,"units":1,"whatsapp_messages_month":2000}'::jsonb, true, 23, now()),
            (gen_random_uuid(), 'locacao', 'essencial', 'Essencial', 9900, 99000, 'BRL',
             '["rentals","inventory","catalog"]'::jsonb,
             '{"assets":50,"rentable_resources":null,"users":3,"units":1,"whatsapp_messages_month":0}'::jsonb, true, 31, now()),
            (gen_random_uuid(), 'locacao', 'profissional', 'Profissional', 18900, 189000, 'BRL',
             '["rentals","inventory","catalog"]'::jsonb,
             '{"assets":300,"rentable_resources":null,"users":8,"units":1,"whatsapp_messages_month":500}'::jsonb, true, 32, now()),
            (gen_random_uuid(), 'locacao', 'completo', 'Completo', 29900, 299000, 'BRL',
             '["rentals","inventory","catalog"]'::jsonb,
             '{"assets":1500,"rentable_resources":null,"users":20,"units":3,"whatsapp_messages_month":2000}'::jsonb, true, 33, now())
        ON CONFLICT (package_key, plan_key) DO NOTHING;
        """;

    /// <summary>
    /// One current subscription for every tenant that has none. The package follows the active modules:
    /// pmoc/os without rentals = manutencao; rentals without pmoc/os plus inventory or catalog = locacao;
    /// anything else (including ambiguous mixes) = reservas. A trial tenant gets a trialing, provider
    /// billed Profissional row whose period ends at trial_ends_at. Every other tenant gets an active,
    /// manually billed Completo row with limits_unlimited and a 100 percent discount, so no existing
    /// tenant is limited, blocked or counted in MRR by the rollout. Existing trial dates are not touched.
    /// </summary>
    public const string BackfillSubscriptions = """
        INSERT INTO billing.tenant_subscriptions
            (id, tenant_id, plan_catalog_id, billing_cycle, status, billing_mode, limits_unlimited,
             current_period_start, current_period_end, discount_percent, row_version, created_at)
        SELECT gen_random_uuid(), t.id, pc.id, 'monthly',
               CASE WHEN t.is_trial THEN 'trialing' ELSE 'active' END,
               CASE WHEN t.is_trial THEN 'provider' ELSE 'manual' END,
               NOT t.is_trial,
               t.created_at,
               CASE WHEN t.is_trial THEN t.trial_ends_at ELSE NULL END,
               CASE WHEN t.is_trial THEN 0 ELSE 100 END,
               uuid_send(gen_random_uuid()),
               now()
        FROM core.tenants t
        CROSS JOIN LATERAL (
            SELECT CASE
                       WHEN m.has_maintenance AND NOT m.has_rentals THEN 'manutencao'
                       WHEN m.has_rentals AND NOT m.has_maintenance AND (m.has_inventory OR m.has_catalog) THEN 'locacao'
                       ELSE 'reservas'
                   END AS package_key
            FROM (
                SELECT COALESCE(bool_or(lower(tm.module_name) IN ('pmoc', 'os')), false) AS has_maintenance,
                       COALESCE(bool_or(lower(tm.module_name) = 'rentals'), false) AS has_rentals,
                       COALESCE(bool_or(lower(tm.module_name) = 'inventory'), false) AS has_inventory,
                       COALESCE(bool_or(lower(tm.module_name) = 'catalog'), false) AS has_catalog
                FROM core.tenant_modules tm
                WHERE tm.tenant_id = t.id AND tm.is_active
            ) m
        ) p
        JOIN billing.plan_catalog pc
            ON pc.package_key = p.package_key
           AND pc.plan_key = CASE WHEN t.is_trial THEN 'profissional' ELSE 'completo' END
        WHERE NOT EXISTS (
            SELECT 1 FROM billing.tenant_subscriptions s
            WHERE s.tenant_id = t.id AND s.ended_at IS NULL);
        """;

    /// <summary>
    /// Insert-only, idempotent: the settings.billing catalog row and its grants to the system Admin and
    /// SuperAdmin roles of existing tenants (new tenants get them from <c>TenantAccessBootstrapper</c>).
    /// </summary>
    public const string GrantBillingPermission = """
        INSERT INTO core.permissions (id, key, name, description, module_key, created_at)
        VALUES
            (gen_random_uuid(), 'core.settings.billing', 'Manage plan and billing', 'View and change the tenant plan, billing data and payment history.', NULL, now())
        ON CONFLICT (key) DO NOTHING;

        INSERT INTO core.role_permissions (role_id, permission_id, granted_at)
        SELECT r.id, p.id, now()
        FROM core.roles r CROSS JOIN core.permissions p
        WHERE r.is_system_role
          AND lower(r.name) IN ('admin', 'superadmin')
          AND p.key = 'core.settings.billing'
        ON CONFLICT DO NOTHING;
        """;
}
