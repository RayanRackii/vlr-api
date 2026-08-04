using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class CleanupFiccDuplicateCpfsAndSeedRegistrationFields : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // 0) Sync CPF from JSON extras into column before dedupe.
        migrationBuilder.Sql(
            """
            UPDATE core.customers c
            SET cpf = regexp_replace(c.extra_attributes->>'cpf', '[^0-9]', '', 'g')
            FROM core.tenants t
            WHERE c.tenant_id = t.id
              AND lower(t.subdomain) = 'ficc'
              AND (c.cpf IS NULL OR btrim(c.cpf) = '')
              AND c.extra_attributes ? 'cpf'
              AND nullif(regexp_replace(c.extra_attributes->>'cpf', '[^0-9]', '', 'g'), '') IS NOT NULL;
            """);

        // 1) Remove duplicate CPF customers for tenant subdomain = ficc (keep oldest).
        migrationBuilder.Sql(
            """
            WITH ficc AS (
                SELECT id
                FROM core.tenants
                WHERE lower(subdomain) = 'ficc'
            ),
            ranked AS (
                SELECT
                    c.id,
                    ROW_NUMBER() OVER (
                        PARTITION BY c.tenant_id, c.cpf
                        ORDER BY c.created_at ASC, c.id ASC
                    ) AS rn
                FROM core.customers c
                INNER JOIN ficc t ON c.tenant_id = t.id
                WHERE c.cpf IS NOT NULL
                  AND btrim(c.cpf) <> ''
            ),
            doomed AS (
                SELECT id FROM ranked WHERE rn > 1
            )
            DELETE FROM rentals.reservation_items ri
            USING rentals.reservations r, doomed d
            WHERE ri.reservation_id = r.id
              AND r.customer_id = d.id;

            WITH ficc AS (
                SELECT id FROM core.tenants WHERE lower(subdomain) = 'ficc'
            ),
            ranked AS (
                SELECT
                    c.id,
                    ROW_NUMBER() OVER (
                        PARTITION BY c.tenant_id, c.cpf
                        ORDER BY c.created_at ASC, c.id ASC
                    ) AS rn
                FROM core.customers c
                INNER JOIN ficc t ON c.tenant_id = t.id
                WHERE c.cpf IS NOT NULL
                  AND btrim(c.cpf) <> ''
            ),
            doomed AS (
                SELECT id FROM ranked WHERE rn > 1
            )
            DELETE FROM rentals.reservations r
            USING doomed d
            WHERE r.customer_id = d.id;

            WITH ficc AS (
                SELECT id FROM core.tenants WHERE lower(subdomain) = 'ficc'
            ),
            ranked AS (
                SELECT
                    c.id,
                    ROW_NUMBER() OVER (
                        PARTITION BY c.tenant_id, c.cpf
                        ORDER BY c.created_at ASC, c.id ASC
                    ) AS rn
                FROM core.customers c
                INNER JOIN ficc t ON c.tenant_id = t.id
                WHERE c.cpf IS NOT NULL
                  AND btrim(c.cpf) <> ''
            ),
            doomed AS (
                SELECT id FROM ranked WHERE rn > 1
            )
            DELETE FROM core.otp_codes o
            USING doomed d
            WHERE o.customer_id = d.id;

            WITH ficc AS (
                SELECT id FROM core.tenants WHERE lower(subdomain) = 'ficc'
            ),
            ranked AS (
                SELECT
                    c.id,
                    ROW_NUMBER() OVER (
                        PARTITION BY c.tenant_id, c.cpf
                        ORDER BY c.created_at ASC, c.id ASC
                    ) AS rn
                FROM core.customers c
                INNER JOIN ficc t ON c.tenant_id = t.id
                WHERE c.cpf IS NOT NULL
                  AND btrim(c.cpf) <> ''
            )
            DELETE FROM core.customers c
            USING ranked r
            WHERE c.id = r.id
              AND r.rn > 1;
            """);

        // 2) Ensure unique index exists (idempotent).
        migrationBuilder.Sql(
            """
            CREATE UNIQUE INDEX IF NOT EXISTS ix_customers_tenant_id_cpf
            ON core.customers (tenant_id, cpf)
            WHERE cpf IS NOT NULL;
            """);

        // 3) Seed FICC registration extras: cpf (required), cep, photo.
        migrationBuilder.Sql(
            """
            INSERT INTO core.tenant_registration_fields (
                id, tenant_id, field_key, label, field_type, is_required, sort_order, options_json, created_at, updated_at
            )
            SELECT
                gen_random_uuid(),
                t.id,
                v.field_key,
                v.label,
                v.field_type,
                v.is_required,
                v.sort_order,
                NULL,
                NOW(),
                NULL
            FROM core.tenants t
            CROSS JOIN (
                VALUES
                    ('cpf', 'CPF', 'cpf', TRUE, 10),
                    ('cep', 'CEP', 'cep', FALSE, 20),
                    ('photo', 'Foto de perfil', 'photo', FALSE, 30)
            ) AS v(field_key, label, field_type, is_required, sort_order)
            WHERE lower(t.subdomain) = 'ficc'
              AND NOT EXISTS (
                  SELECT 1
                  FROM core.tenant_registration_fields f
                  WHERE f.tenant_id = t.id
                    AND f.field_key = v.field_key
              );
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DELETE FROM core.tenant_registration_fields f
            USING core.tenants t
            WHERE f.tenant_id = t.id
              AND lower(t.subdomain) = 'ficc'
              AND f.field_key IN ('cpf', 'cep', 'photo');
            """);
    }
}
