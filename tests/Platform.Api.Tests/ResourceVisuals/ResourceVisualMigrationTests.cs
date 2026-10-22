using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Infrastructure.Persistence.Migrations;

namespace Platform.Api.Tests.ResourceVisuals;

public sealed class ResourceVisualMigrationTests
{
    [Fact]
    public void Map_extension_audits_legacy_links_before_FK_changes_and_preserves_legacy_tables_on_rollback()
    {
        var migration = new ExtendRentalMapsAndVisualAssignments();
        var preflight = Assert.IsType<SqlOperation>(migration.UpOperations[0]).Sql;
        Assert.Contains("BOOKING_MAP_PREFLIGHT_FAILED", preflight);
        Assert.Contains("LOCK TABLE", preflight);
        Assert.Contains("l.tenant_id = i.tenant_id", preflight);
        Assert.Contains("r.tenant_id = i.tenant_id", preflight);
        Assert.DoesNotContain(migration.UpOperations, op => op is DropTableOperation or DropColumnOperation or DeleteDataOperation);
        Assert.Equal(new[] { "booking_map_settings", "layout_decorations" },
            migration.DownOperations.OfType<DropTableOperation>().Select(op => op.Name).Order().ToArray());
        Assert.All(migration.DownOperations.OfType<DropColumnOperation>(), op =>
            Assert.Contains(op.Name, new[] { "capacity_people", "visual_template_revision_id", "sort_order", "version", "lock_aspect_ratio", "rotation" }));
        Assert.Contains(migration.DownOperations.OfType<AddForeignKeyOperation>(), op =>
            op.Table == "layouts" && op.Columns.SequenceEqual(new[] { "unit_id" }) && op.OnDelete == ReferentialAction.SetNull);
    }

    [Fact]
    public void Seed_rollback_is_guarded_and_permissions_are_tenant_only_with_administrative_grants()
    {
        var seed = new SeedResourceVisualCatalogV1();
        Assert.Single(seed.UpOperations);
        Assert.Single(seed.DownOperations);
        var up = Assert.Single(seed.UpOperations.OfType<SqlOperation>()).Sql;
        var down = Assert.Single(seed.DownOperations.OfType<SqlOperation>()).Sql;
        Assert.Contains("ON CONFLICT DO NOTHING", up);
        Assert.DoesNotContain("UPDATE ", up, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RESOURCE_VISUAL_SEED_DOWN_BLOCKED", down);
        Assert.Contains("visual_template_revision_id", down);
        Assert.DoesNotContain("DELETE FROM rentals.", down);
        var permissions = Assert.Single(new AddBookingMapPermissions().UpOperations.OfType<SqlOperation>()).Sql;
        foreach (var key in new[] { "rentals.segments.read", "rentals.segments.write", "rentals.map_settings.read", "rentals.map_settings.write" })
            Assert.Contains(key, permissions);
        Assert.Contains("('admin', 'superadmin')", permissions);
        Assert.DoesNotContain("platform.", permissions);
    }

    [Fact]
    public async Task Snapshot_matches_current_relational_model()
    {
        await using var factory = new PostgresAppDbFactory("Host=127.0.0.1;Port=1;Database=offline;Username=offline;Password=offline");
        await using var db = factory.Create(new FakeTenantProvider());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void Unit_write_migration_grants_and_rolls_back_only_canonical_system_roles()
    {
        var migration = new AddUnitWriteAndVisualTemplateDefaults();
        var up = Assert.Single(migration.UpOperations.OfType<SqlOperation>()).Sql;
        var down = Assert.Single(migration.DownOperations.OfType<SqlOperation>()).Sql;

        Assert.Contains("r.is_system_role", up);
        Assert.Contains("lower(r.name) IN ('admin', 'superadmin')", up);
        Assert.Contains("r.is_system_role", down);
        Assert.Contains("lower(r.name) IN ('admin', 'superadmin')", down);
    }
}
