using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Platform.Api.Modules.Pmoc.Dtos;
using Platform.Api.Modules.Pmoc.Services;
using Platform.Api.Tests.Assets;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence.Seed;

namespace Platform.Api.Tests.Pmoc;

public sealed class PmocPhase1FoundationTests
{
    [Fact]
    public void New_maintenance_plan_defaults_auto_generate_to_false()
    {
        var plan = new MaintenancePlan
        {
            TenantId = Guid.NewGuid(),
            UnitId = Guid.NewGuid(),
            Name = "Test",
            IntervalDays = 30,
            FirstDueDate = new DateOnly(2026, 9, 1),
            AssetCategoryId = Guid.NewGuid(),
            IsActive = true,
        };

        Assert.False(plan.AutoGenerateEnabled);
        Assert.Equal(MaintenancePlanOriginKind.Custom, plan.OriginKind);
        Assert.Null(plan.SourceTemplateId);
        Assert.Null(plan.SourceTemplateVersion);
    }

    [Fact]
    public void Seed_template_guid_is_unchanged()
    {
        Assert.Equal(
            Guid.Parse("6f1c2a0e-4b9d-4f3a-9c7e-1d2a3b4c5d6e"),
            GlobalTemplateSeed.AnvisaNr10TemplateId);
    }

    [Fact]
    public void Seed_template_has_phase1_library_metadata()
    {
        Assert.Equal("pmoc-ar-condicionado-anvisa-nr10", GlobalTemplateSeed.AnvisaLibraryKey);
        Assert.Equal(1, GlobalTemplateSeed.AnvisaVersion);
        Assert.Equal(GlobalTemplateStatus.Published, GlobalTemplateSeed.AnvisaStatus);
        Assert.Contains("Lei 13.589/2018", GlobalTemplateSeed.AnvisaSourceReferences, StringComparison.Ordinal);
        Assert.Contains("Anvisa RE 09", GlobalTemplateSeed.AnvisaSourceReferences, StringComparison.Ordinal);
        Assert.Contains("NR-10", GlobalTemplateSeed.AnvisaSourceReferences, StringComparison.Ordinal);
        Assert.DoesNotContain("CREA", GlobalTemplateSeed.AnvisaSourceReferences, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Model_configures_library_key_version_uniqueness()
    {
        using var db = CreateModelDb();
        var entity = db.Model.FindEntityType(typeof(GlobalMaintenanceTemplate));
        Assert.NotNull(entity);

        var index = entity.GetIndexes().Single(item =>
            item.Properties.Select(property => property.Name)
                .SequenceEqual(["LibraryKey", "Version"]));

        Assert.True(index.IsUnique);
    }

    [Fact]
    public void Model_seed_preserves_template_id_and_metadata()
    {
        using var db = CreateModelDb();
        var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(GlobalMaintenanceTemplate));
        Assert.NotNull(entity);

        var seed = entity.GetSeedData().Single();
        Assert.Equal(GlobalTemplateSeed.AnvisaNr10TemplateId, seed["Id"]);
        Assert.Equal(GlobalTemplateSeed.AnvisaLibraryKey, seed["LibraryKey"]);
        Assert.Equal(GlobalTemplateSeed.AnvisaVersion, seed["Version"]);
        Assert.Equal(GlobalTemplateSeed.AnvisaStatus, seed["Status"]);
        Assert.Equal(GlobalTemplateSeed.AnvisaSourceReferences, seed["SourceReferences"]);
    }

    [Fact]
    public void Model_work_order_plan_fk_is_restrict()
    {
        using var db = CreateModelDb();
        var entity = db.Model.FindEntityType(typeof(WorkOrder));
        Assert.NotNull(entity);

        var fk = entity.GetForeignKeys().Single(item =>
            item.Properties.Count == 1
            && item.Properties[0].Name == "MaintenancePlanId");

        Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
    }

    [Fact]
    public void Model_work_order_has_source_plan_name()
    {
        using var db = CreateModelDb();
        var entity = db.Model.FindEntityType(typeof(WorkOrder));
        Assert.NotNull(entity);

        var property = entity.FindProperty(nameof(WorkOrder.SourcePlanName));
        Assert.NotNull(property);
        Assert.Equal(200, property.GetMaxLength());
        Assert.True(property.IsNullable);
    }

    [Fact]
    public void Model_auto_generate_enabled_is_required_without_true_store_default()
    {
        using var db = CreateModelDb();
        var entity = db.Model.FindEntityType(typeof(MaintenancePlan));
        Assert.NotNull(entity);

        var property = entity.FindProperty(nameof(MaintenancePlan.AutoGenerateEnabled));
        Assert.NotNull(property);
        Assert.False(property.IsNullable);
        Assert.NotEqual(true, property.GetDefaultValue());
    }

    [Fact]
    public void Model_pmoc_period_index_is_filtered_unique()
    {
        using var db = CreateModelDb();
        var entity = db.Model.FindEntityType(typeof(WorkOrder));
        Assert.NotNull(entity);

        var index = entity.GetIndexes().Single(item =>
            item.GetDatabaseName() == "ux_os_work_orders_pmoc_period");

        Assert.True(index.IsUnique);
        Assert.Equal(
            new[] { "TenantId", "MaintenancePlanId", "AssetId", "ScheduledDate" },
            index.Properties.Select(property => property.Name).ToArray());
        Assert.Equal(
            "maintenance_plan_id IS NOT NULL AND status <> 'Canceled'",
            index.GetFilter());
    }

    [Fact]
    public async Task Create_plan_stamps_custom_origin_and_auto_generate_false()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var service = new MaintenancePlanService(
            harness.Db,
            harness.TenantProvider,
            harness.CreateRegistry());

        var created = await service.CreatePlanWithTasksAsync(
            new CreateMaintenancePlanRequest
            {
                UnitId = harness.UnitId,
                Name = "Plano novo",
                IntervalDays = 30,
            FirstDueDate = new DateOnly(2026, 9, 1),
                AssetCategoryId = harness.CategoryId,
                Tasks =
                [
                    new CreatePlanTaskDto
                    {
                        Title = "Filtro",
                        InputType = TaskInputType.Checkbox,
                        Order = 1,
                    },
                ],
            },
            CancellationToken.None);

        var stored = await harness.Db.MaintenancePlans.SingleAsync(plan => plan.Id == created.Id);
        Assert.False(stored.AutoGenerateEnabled);
        Assert.Equal(MaintenancePlanOriginKind.Custom, stored.OriginKind);
        Assert.Null(stored.SourceTemplateId);
        Assert.Null(stored.SourceTemplateVersion);
    }

    [Fact]
    public void Migration_a_backfills_existing_plans_true_then_defaults_false()
    {
        var up = ReadMigrationUp("AddPmocOsPhase1Foundation");

        Assert.Contains("name: \"auto_generate_enabled\"", up, StringComparison.Ordinal);
        Assert.Contains("defaultValue: true", up, StringComparison.Ordinal);
        Assert.Contains("ALTER COLUMN auto_generate_enabled SET DEFAULT false", up, StringComparison.Ordinal);
        Assert.Contains("defaultValue: \"Custom\"", up, StringComparison.Ordinal);
        Assert.Contains("source_plan_name = p.name", up, StringComparison.Ordinal);
        Assert.Contains("ReferentialAction.Restrict", up, StringComparison.Ordinal);
        Assert.Contains("6f1c2a0e-4b9d-4f3a-9c7e-1d2a3b4c5d6e", up, StringComparison.Ordinal);
        Assert.DoesNotContain("ux_os_work_orders_pmoc_period", up, StringComparison.Ordinal);
        Assert.DoesNotContain("SET DEFAULT true", up, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_b_replaces_period_index_with_filtered_unique()
    {
        var up = ReadMigrationUp("AddPmocOsWorkOrderPeriodUnique");

        Assert.Contains(
            "ix_work_orders_tenant_id_maintenance_plan_id_asset_id_schedule",
            up,
            StringComparison.Ordinal);
        Assert.Contains("DropIndex", up, StringComparison.Ordinal);
        Assert.Contains("ux_os_work_orders_pmoc_period", up, StringComparison.Ordinal);
        Assert.Contains("unique: true", up, StringComparison.Ordinal);
        Assert.Contains(
            "maintenance_plan_id IS NOT NULL AND status <> 'Canceled'",
            up,
            StringComparison.Ordinal);
        Assert.DoesNotContain("auto_generate_enabled", up, StringComparison.Ordinal);
    }

    [Fact]
    public void Generation_job_is_fail_closed_until_slice_3()
    {
        var source = File.ReadAllText(FindRepoFile(Path.Combine("Platform.Api", "Jobs", "PmocEngineJob.cs")));
        Assert.Contains("fail-closed until Phase 3 Slice 3", source, StringComparison.Ordinal);
        Assert.Contains("Skipping all plans", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GenerateAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new WorkOrder", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PmocDueCalendar", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IsDueToday", source, StringComparison.Ordinal);
    }

    private static AppDbContext CreateModelDb()
    {
        return InMemoryAppDb.Create(new FakeTenantProvider { TenantId = Guid.NewGuid() });
    }

    private static string ReadMigrationUp(string migrationName)
    {
        var migrationsDir = Path.Combine(
            FindRepoRoot(),
            "Core",
            "Platform.Core.Infrastructure",
            "Persistence",
            "Migrations");
        var files = Directory.GetFiles(migrationsDir, $"*{migrationName}*.cs")
            .Where(path => !path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.True(files.Length == 1, $"Expected one migration named {migrationName} in {migrationsDir}");
        return File.ReadAllText(files[0]).Split("protected override void Down", 2)[0];
    }

    private static string FindRepoFile(string relativePath)
    {
        return Path.Combine(FindRepoRoot(), relativePath);
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
