using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;

namespace Platform.Api.Tests.Pmoc;

public sealed class PmocPhase3MigrationSqlTests
{
    [Fact]
    public void Migration_deletes_only_pmoc_linked_work_orders_then_plans()
    {
        var up = ReadMigrationUp("ApplyPmocOsPhase3FinalScheduling");

        Assert.Contains("DELETE FROM os.work_orders", up, StringComparison.Ordinal);
        Assert.Contains("WHERE maintenance_plan_id IS NOT NULL", up, StringComparison.Ordinal);
        Assert.Contains("DELETE FROM pmoc.maintenance_plans", up, StringComparison.Ordinal);

        var deleteWorkOrdersIndex = up.IndexOf("DELETE FROM os.work_orders", StringComparison.Ordinal);
        var deletePlansIndex = up.IndexOf("DELETE FROM pmoc.maintenance_plans", StringComparison.Ordinal);
        var dropFrequencyIndex = up.IndexOf("DropColumn", StringComparison.Ordinal);
        Assert.True(deleteWorkOrdersIndex >= 0 && deleteWorkOrdersIndex < deletePlansIndex);
        Assert.True(deletePlansIndex < dropFrequencyIndex);

        Assert.Contains("name: \"frequency\"", up, StringComparison.Ordinal);
        Assert.Contains("table: \"maintenance_plans\"", up, StringComparison.Ordinal);
        Assert.Contains("table: \"global_maintenance_templates\"", up, StringComparison.Ordinal);
        Assert.Contains("first_due_date", up, StringComparison.Ordinal);
        Assert.Contains("interval_days", up, StringComparison.Ordinal);
        Assert.Contains("ck_maintenance_plans_interval_days", up, StringComparison.Ordinal);
        Assert.Contains("interval_days >= 1 AND interval_days <= 3650", up, StringComparison.Ordinal);

        Assert.DoesNotContain("DELETE FROM core.tenants", up, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE FROM core.units", up, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE FROM core.assets", up, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE FROM core.users", up, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE FROM core.permissions", up, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE FROM rentals.", up, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE FROM inventory.", up, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE FROM ficc.", up, StringComparison.Ordinal);
        Assert.DoesNotContain("TRUNCATE", up, StringComparison.Ordinal);
        Assert.DoesNotContain("__ef_migrations_history", up, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE", up, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_has_interval_days_check_and_no_frequency()
    {
        using var db = InMemoryAppDb.Create(new FakeTenantProvider { TenantId = Guid.NewGuid() });
        var plan = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(MaintenancePlan));
        Assert.NotNull(plan);
        Assert.NotNull(plan!.FindProperty(nameof(MaintenancePlan.IntervalDays)));
        Assert.NotNull(plan.FindProperty(nameof(MaintenancePlan.FirstDueDate)));
        Assert.Null(plan.FindProperty("Frequency"));
        Assert.Contains(
            plan.GetCheckConstraints(),
            constraint => constraint.Name == "ck_maintenance_plans_interval_days");

        var template = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(GlobalMaintenanceTemplate));
        Assert.NotNull(template);
        Assert.Null(template!.FindProperty("Frequency"));
        Assert.Null(template.FindProperty("IntervalDays"));
        Assert.Null(template.FindProperty("FirstDueDate"));
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
        Assert.True(files.Length == 1, $"Expected one migration named {migrationName}");
        return File.ReadAllText(files[0]).Split("protected override void Down", 2)[0];
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
