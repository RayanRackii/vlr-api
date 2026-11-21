using Microsoft.EntityFrameworkCore;
using System.Collections;
using System.Reflection;
using System.Text;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
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
        if (!db.Database.HasPendingModelChanges())
        {
            return;
        }

        // Same computation as HasPendingModelChanges, but keep the operations so a failure says what differs.
        var differ = db.GetService<IMigrationsModelDiffer>();
        var snapshotModel = db.GetService<IMigrationsAssembly>().ModelSnapshot?.Model;
        if (snapshotModel is IMutableModel mutable)
        {
            try
            {
                snapshotModel = mutable.FinalizeModel();
            }
            catch (InvalidOperationException)
            {
                // Already finalized (read-only): use it as is.
            }
        }

        if (snapshotModel is not null)
        {
            snapshotModel = db.GetService<IModelRuntimeInitializer>().Initialize(snapshotModel);
        }

        var operations = differ.GetDifferences(
            snapshotModel?.GetRelationalModel(),
            db.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        var report = new StringBuilder("The model snapshot does not match the EF model. Operations that a new migration would contain:");
        foreach (var operation in operations)
        {
            report.AppendLine();
            Describe(report, operation, 1, 0);
        }

        Assert.Fail(report.ToString());
    }

    private static void Describe(StringBuilder report, object value, int indent, int depth)
    {
        var pad = new string(' ', indent * 2);
        report.Append(pad).Append(value.GetType().Name).AppendLine();
        foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || property.Name is "Item" or "IsDestructiveChange")
            {
                continue;
            }

            object? member;
            try
            {
                member = property.GetValue(value);
            }
            catch
            {
                continue;
            }

            switch (member)
            {
                case null:
                    continue;
                case string or bool or int or long or Type or Enum:
                    if (member is bool b && !b && property.Name != "IsNullable")
                    {
                        continue;
                    }

                    report.Append(pad).Append("  ").Append(property.Name).Append(" = ").AppendLine(member.ToString());
                    break;
                case IEnumerable items when depth < 3:
                    var list = items.Cast<object?>().Where(i => i is not null).ToList();
                    if (list.Count == 0)
                    {
                        continue;
                    }

                    report.Append(pad).Append("  ").Append(property.Name).AppendLine(":");
                    foreach (var item in list)
                    {
                        if (item is string text)
                        {
                            report.Append(pad).Append("    ").AppendLine(text);
                        }
                        else
                        {
                            Describe(report, item!, indent + 2, depth + 1);
                        }
                    }

                    break;
                case MigrationOperation nested when depth < 3:
                    report.Append(pad).Append("  ").Append(property.Name).AppendLine(":");
                    Describe(report, nested, indent + 2, depth + 1);
                    break;
            }
        }

        if (value is MigrationOperation operation)
        {
            foreach (var annotation in operation.GetAnnotations())
            {
                report.Append(pad).Append("  @").Append(annotation.Name).Append(" = ").AppendLine(annotation.Value?.ToString());
            }
        }
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
