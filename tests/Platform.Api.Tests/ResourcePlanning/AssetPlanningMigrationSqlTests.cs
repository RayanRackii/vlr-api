using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Common;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence.Migrations;

namespace Platform.Api.Tests.ResourcePlanning;

/// <summary>
/// Structure of migration <c>AddAssetPlanning</c> (slice API-2): additive, pure create, tenant
/// scoped, guarded <c>Down</c>. No database needed; the model snapshot is covered by
/// <c>Snapshot_matches_current_relational_model</c>.
/// </summary>
public sealed class AssetPlanningMigrationSqlTests
{
    private const string MigrationName = "AddAssetPlanning";

    private static readonly string[] PlanningTables =
        ["asset_planning_profiles", "asset_working_hours", "asset_allocations"];

    [Fact]
    public void Up_is_additive_with_no_data_statements_and_no_drops()
    {
        var operations = new AddAssetPlanning().UpOperations;

        Assert.All(operations, operation => Assert.True(
            operation is EnsureSchemaOperation or AddUniqueConstraintOperation or CreateTableOperation or CreateIndexOperation,
            $"Unexpected operation in an additive migration: {operation.GetType().Name}"));
        Assert.DoesNotContain(operations, operation => operation is SqlOperation);

        var up = ReadMigrationSource().Split("protected override void Down", 2)[0];
        foreach (var forbidden in new[]
        {
            "DELETE FROM", "TRUNCATE", "DROP TABLE", "DROP COLUMN", "UPDATE ", "INSERT INTO", "ALTER COLUMN",
            "__ef_migrations_history", "DropColumn", "DropTable", "AlterColumn", "Sql(",
        })
        {
            Assert.DoesNotContain(forbidden, up, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Up_creates_schema_planning_and_exactly_the_three_tables_in_it()
    {
        var operations = new AddAssetPlanning().UpOperations;

        Assert.Equal("planning", Assert.Single(operations.OfType<EnsureSchemaOperation>()).Name);
        var created = operations.OfType<CreateTableOperation>().ToArray();
        Assert.Equal(PlanningTables.Order().ToArray(), created.Select(t => t.Name).Order().ToArray());
        Assert.All(created, table => Assert.Equal("planning", table.Schema));
    }

    [Fact]
    public void Every_table_has_tenant_id_and_a_tenant_qualified_foreign_key_to_the_asset()
    {
        var created = new AddAssetPlanning().UpOperations.OfType<CreateTableOperation>().ToArray();

        foreach (var table in created)
        {
            var tenant = Assert.Single(table.Columns, column => column.Name == "tenant_id");
            Assert.False(tenant.IsNullable);

            var assetFk = Assert.Single(
                table.ForeignKeys,
                fk => fk.PrincipalTable == "assets" && fk.PrincipalSchema == "assets");
            Assert.Equal(["tenant_id", "asset_id"], assetFk.Columns);
            Assert.Equal(["tenant_id", "id"], assetFk.PrincipalColumns);

            // No single-column FK anywhere: every reference is tenant-qualified.
            Assert.All(table.ForeignKeys, fk =>
            {
                Assert.Equal("tenant_id", fk.Columns[0]);
                Assert.Equal(["tenant_id", "id"], fk.PrincipalColumns);
            });
        }
    }

    [Fact]
    public void Allocations_link_to_catalog_work_orders_and_reservations_with_restrict_and_a_single_link_check()
    {
        var allocations = new AddAssetPlanning().UpOperations.OfType<CreateTableOperation>()
            .Single(t => t.Name == "asset_allocations");

        Assert.Equal(
            ["assets", "catalog_order_items", "reservation_items", "work_orders"],
            allocations.ForeignKeys.Select(fk => fk.PrincipalTable).Order().ToArray());
        Assert.All(allocations.ForeignKeys, fk => Assert.Equal(ReferentialAction.Restrict, fk.OnDelete));

        var checks = allocations.CheckConstraints.ToDictionary(c => c.Name, c => c.Sql);
        Assert.Equal("catalog_order_item_id IS NULL OR work_order_id IS NULL", checks["ck_asset_allocations_single_link"]);
        Assert.Equal("planned_work_end > planned_work_start", checks["ck_asset_allocations_work_window"]);
        Assert.Equal(
            "occupied_start <= planned_work_start AND occupied_end >= planned_work_end",
            checks["ck_asset_allocations_occupied_covers_work"]);
        Assert.Equal("quantity >= 1", checks["ck_asset_allocations_quantity"]);

        var indexes = new AddAssetPlanning().UpOperations.OfType<CreateIndexOperation>()
            .Where(i => i.Table == "asset_allocations")
            .ToDictionary(i => i.Name);
        Assert.Equal("status IN ('Scheduled', 'Running', 'Cleaning')", indexes["ix_asset_allocations_tenant_asset_blocking"].Filter);
        Assert.True(indexes["ux_asset_allocations_tenant_reservation_item"].IsUnique);
        Assert.Equal("reservation_item_id IS NOT NULL", indexes["ux_asset_allocations_tenant_reservation_item"].Filter);
    }

    [Fact]
    public void Profile_and_hours_carry_the_spec_checks_and_unique_indexes()
    {
        var operations = new AddAssetPlanning().UpOperations;
        var profiles = operations.OfType<CreateTableOperation>().Single(t => t.Name == "asset_planning_profiles");
        var hours = operations.OfType<CreateTableOperation>().Single(t => t.Name == "asset_working_hours");

        Assert.Equal(
            ["ck_asset_planning_profiles_capacity", "ck_asset_planning_profiles_default_cleanup_minutes", "ck_asset_planning_profiles_default_prep_minutes"],
            profiles.CheckConstraints.Select(c => c.Name).Order().ToArray());
        Assert.Equal(
            ["ck_asset_working_hours_close_after_open", "ck_asset_working_hours_day_of_week"],
            hours.CheckConstraints.Select(c => c.Name).Order().ToArray());
        Assert.Equal("close_time > open_time", hours.CheckConstraints.Single(c => c.Name == "ck_asset_working_hours_close_after_open").Sql);

        var profileIndex = operations.OfType<CreateIndexOperation>().Single(i => i.Table == "asset_planning_profiles");
        Assert.True(profileIndex.IsUnique);
        Assert.Equal(["tenant_id", "asset_id"], profileIndex.Columns);

        var hoursIndex = operations.OfType<CreateIndexOperation>().Single(i => i.Table == "asset_working_hours");
        Assert.True(hoursIndex.IsUnique);
        Assert.Equal(["tenant_id", "asset_id", "day_of_week", "open_time"], hoursIndex.Columns);
    }

    [Fact]
    public void Only_two_existing_tables_are_touched_with_a_unique_constraint_on_tenant_id_and_id()
    {
        var constraints = new AddAssetPlanning().UpOperations.OfType<AddUniqueConstraintOperation>().ToArray();

        Assert.Equal(
            [("assets", "assets"), ("os", "work_orders")],
            constraints.Select(c => (c.Schema!, c.Table)).Order().ToArray());
        Assert.All(constraints, c => Assert.Equal(["tenant_id", "id"], c.Columns));
    }

    [Fact]
    public void Down_refuses_before_any_destructive_step_when_planning_rows_exist()
    {
        var down = new AddAssetPlanning().DownOperations;

        var guard = Assert.IsType<SqlOperation>(down[0]);
        foreach (var table in PlanningTables)
        {
            Assert.Contains($"planning.{table}", guard.Sql, StringComparison.Ordinal);
        }

        Assert.Contains("RAISE EXCEPTION", guard.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(down.Skip(1), operation => operation is SqlOperation);

        // Everything destructive comes after the guard, and the schema is not dropped with CASCADE.
        Assert.Equal(PlanningTables.Order().ToArray(), down.OfType<DropTableOperation>().Select(o => o.Name).Order().ToArray());
        Assert.DoesNotContain(
            "CASCADE",
            ReadMigrationSource().Split("protected override void Down", 2)[1],
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["assets", "os"], down.OfType<DropUniqueConstraintOperation>().Select(o => o.Schema!).Order().ToArray());
    }

    [Fact]
    public void Model_maps_the_three_entities_tenant_scoped_with_a_tenant_qualified_asset_key()
    {
        using var db = InMemoryAppDb.Create(new FakeTenantProvider { TenantId = Guid.NewGuid() });
        var model = db.GetService<IDesignTimeModel>().Model;

        foreach (var clr in new[] { typeof(AssetPlanningProfile), typeof(AssetWorkingHours), typeof(AssetAllocation) })
        {
            var entity = model.FindEntityType(clr);
            Assert.NotNull(entity);
            Assert.True(typeof(ITenantScoped).IsAssignableFrom(clr));
            Assert.NotNull(entity!.FindProperty("TenantId"));
            var assetFk = Assert.Single(
                entity.GetForeignKeys(),
                fk => fk.PrincipalEntityType.ClrType == typeof(Asset));
            Assert.Equal(["TenantId", "AssetId"], assetFk.Properties.Select(p => p.Name).ToArray());
            Assert.Equal(["TenantId", "Id"], assetFk.PrincipalKey.Properties.Select(p => p.Name).ToArray());
        }

        var asset = model.FindEntityType(typeof(Asset))!;
        Assert.Contains(
            asset.GetKeys(),
            key => key.Properties.Select(p => p.Name).SequenceEqual(new[] { "TenantId", "Id" }));
    }

    private static string ReadMigrationSource()
    {
        var migrationsDir = Path.Combine(
            FindRepoRoot(),
            "Core",
            "Platform.Core.Infrastructure",
            "Persistence",
            "Migrations");
        var files = Directory.GetFiles(migrationsDir, $"*{MigrationName}*.cs")
            .Where(path => !path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.True(files.Length == 1, $"Expected one migration named {MigrationName}");
        return File.ReadAllText(files[0]);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Core", "Platform.Core.Infrastructure")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate vlr-api repository root.");
    }
}
