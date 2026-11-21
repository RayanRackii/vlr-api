using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Core.Infrastructure.Persistence.Migrations;

/// <summary>
/// Data only (review P1-D). Teacher lessons and the booking map UI used to be gated by tenant and
/// rental-asset ids written in code (<c>FiccTeacherLessonScope</c>) and in appsettings
/// (<c>Rentals:BookingUi:EnabledTenantIds</c>). Both now read tenant settings
/// (<c>rentals.teacher_lessons.rental_asset_ids</c>, <c>rentals.booking_ui.enabled</c>). This migration
/// writes the rows for the tenants that were hard-coded, so nothing changes for them. It is the one
/// place these ids may appear. A tenant that does not exist in the database being migrated (PROD has
/// one of the two, DEV the other) is skipped, and an existing row is never overwritten, so the
/// migration is idempotent and respects a value set through the admin endpoint. No model change.
/// </summary>
public partial class SeedTenantSettingsForTeacherLessonsAndBookingUi : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            INSERT INTO core.tenant_settings (id, tenant_id, key, value, created_at)
            SELECT gen_random_uuid(), v.tenant_id, v.key, v.value, now()
            FROM (VALUES
                ('3ae86b53-d43e-42b0-9616-4833d02f7a1e'::uuid, 'rentals.teacher_lessons.rental_asset_ids',
                 '["04721eb7-d6c2-4197-9930-a8874de5ad27","32b9bd39-9e32-4f25-ad6d-67117c882a3c","35fa8602-f7c1-4876-9b18-dd1f8e8a546b","4f586a63-a89e-467b-b139-a3125a2ba9ec","98259729-fa57-4b5a-abcf-1d36603fc96e","edb91e39-e03d-42f5-b38a-518a5427344b"]'),
                ('b27c9a0a-d585-4626-8c13-1468c5a43519'::uuid, 'rentals.teacher_lessons.rental_asset_ids',
                 '["e56e4f22-f45e-486d-a80b-a3714d6e3d5e","e82cbc28-0fdc-4d44-a062-2efede0f16b8"]'),
                ('3ae86b53-d43e-42b0-9616-4833d02f7a1e'::uuid, 'rentals.booking_ui.enabled', 'true'),
                ('b27c9a0a-d585-4626-8c13-1468c5a43519'::uuid, 'rentals.booking_ui.enabled', 'true')
            ) AS v(tenant_id, key, value)
            WHERE EXISTS (SELECT 1 FROM core.tenants t WHERE t.id = v.tenant_id)
            ON CONFLICT (tenant_id, key) DO NOTHING;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Guarded no-op. The rows cannot be told apart from values an admin set afterwards, and
        // deleting them would silently turn the features off for those tenants.
    }
}
