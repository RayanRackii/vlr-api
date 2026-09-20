using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Jobs;
using Platform.Api.Modules.Pmoc.Dtos;
using Platform.Api.Modules.Pmoc.Services;
using Platform.Api.Modules.WorkOrders.Dtos;
using Platform.Api.Modules.WorkOrders.Services;
using Platform.Api.Tests.Assets;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.WorkOrders;

public sealed class PmocEngineJobTests
{
    [Fact]
    public async Task Job_generates_zero_work_orders_when_active_auto_generate_plan_has_eligible_asset()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        await SeedIntervalPlanWithAssetAsync(harness, autoGenerateEnabled: true, isActive: true);
        harness.TenantProvider.TenantId = null;
        var recorder = new RecordingGenerationService();

        await new PmocEngineJob(harness.Db, recorder, NullLogger<PmocEngineJob>.Instance)
            .ExecuteAsync(CancellationToken.None);

        Assert.Equal(0, recorder.Calls);
        Assert.Empty(await harness.Db.WorkOrders.ToListAsync());
    }

    [Fact]
    public async Task Job_generates_zero_across_tenants()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        await SeedIntervalPlanWithAssetAsync(
            harness,
            autoGenerateEnabled: true,
            isActive: true,
            planName: "Tenant A");
        var tenantB = new Tenant("Club B", "88888888000193", subdomain: "pmoc-job-b");
        var unitB = new Unit(tenantB.Id, "Filial");
        var categoryB = new AssetCategory { TenantId = tenantB.Id, Name = "AC" };
        harness.Db.Tenants.Add(tenantB);
        harness.Db.Units.Add(unitB);
        harness.Db.AssetCategories.Add(categoryB);
        await harness.Db.SaveChangesAsync();
        var planB = new MaintenancePlan
        {
            TenantId = tenantB.Id,
            UnitId = unitB.Id,
            Name = "Tenant B",
            IntervalDays = 1,
            FirstDueDate = HangfireExtensions.GetBrazilToday().AddDays(-1),
            AssetCategoryId = categoryB.Id,
            IsActive = true,
            AutoGenerateEnabled = true,
        };
        planB.AddTask(new PlanTask
        {
            TenantId = tenantB.Id,
            MaintenancePlanId = planB.Id,
            Title = "Filtro B",
            InputType = TaskInputType.Checkbox,
            IsMandatory = true,
            Order = 1,
        });
        harness.Db.MaintenancePlans.Add(planB);
        harness.Db.Assets.Add(new Asset
        {
            TenantId = tenantB.Id,
            UnitId = unitB.Id,
            CategoryId = categoryB.Id,
            FamilyId = harness.FamilyId,
            Name = "Split B",
            Tag = "AC-B",
            Status = AssetStatus.Active,
        });
        await harness.Db.SaveChangesAsync();
        harness.TenantProvider.TenantId = null;
        var recorder = new RecordingGenerationService();

        await new PmocEngineJob(harness.Db, recorder, NullLogger<PmocEngineJob>.Instance)
            .ExecuteAsync(CancellationToken.None);

        Assert.Equal(0, recorder.Calls);
        Assert.Empty(await harness.Db.WorkOrders.ToListAsync());
    }

    [Fact]
    public async Task Job_second_run_still_generates_zero()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        await SeedIntervalPlanWithAssetAsync(harness, autoGenerateEnabled: true, isActive: true);
        harness.TenantProvider.TenantId = null;
        var job = CreateJob(harness);

        await job.ExecuteAsync(CancellationToken.None);
        await job.ExecuteAsync(CancellationToken.None);

        Assert.Empty(await harness.Db.WorkOrders.ToListAsync());
    }

    [Fact]
    public void Job_source_is_fail_closed_and_hangfire_registration_remains()
    {
        var jobSource = File.ReadAllText(FindRepoFile(Path.Combine("Platform.Api", "Jobs", "PmocEngineJob.cs")));
        Assert.Contains("fail-closed until Phase 3 Slice 3", jobSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GenerateAsync", jobSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new WorkOrder", jobSource, StringComparison.Ordinal);
        Assert.DoesNotContain("PmocDueCalendar", jobSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IsDueToday", jobSource, StringComparison.Ordinal);
        Assert.DoesNotContain("FirstDueDate as plan-level due", jobSource, StringComparison.Ordinal);

        var hangfire = File.ReadAllText(FindRepoFile(Path.Combine("Platform.Api", "Jobs", "HangfireExtensions.cs")));
        Assert.Contains("PmocEngineJobId = \"pmoc-engine\"", hangfire, StringComparison.Ordinal);
        Assert.Contains("\"0 6 * * *\"", hangfire, StringComparison.Ordinal);
        Assert.Contains("WorkerCount = 1", hangfire, StringComparison.Ordinal);
        Assert.Contains("ResolveBrazilTimeZone()", hangfire, StringComparison.Ordinal);
    }

    private static PmocEngineJob CreateJob(BulkCreateAssetsHarness harness) =>
        new(harness.Db, new RecordingGenerationService(), NullLogger<PmocEngineJob>.Instance);

    private static async Task<(MaintenancePlanResponse Plan, Asset? Asset)> SeedIntervalPlanWithAssetAsync(
        BulkCreateAssetsHarness harness,
        bool autoGenerateEnabled,
        bool isActive,
        string planName = "Interval PMOC")
    {
        var plan = await new MaintenancePlanService(
            harness.Db,
            harness.TenantProvider,
            harness.CreateRegistry()).CreatePlanWithTasksAsync(
            new CreateMaintenancePlanRequest
            {
                UnitId = harness.UnitId,
                Name = planName,
                IntervalDays = 1,
                FirstDueDate = HangfireExtensions.GetBrazilToday().AddDays(-10),
                AssetCategoryId = harness.CategoryId,
                IsActive = isActive,
                AutoGenerateEnabled = autoGenerateEnabled,
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

        var asset = await CreateMatchingAssetAsync(harness);
        return (plan, asset);
    }

    private static async Task<Asset> CreateMatchingAssetAsync(BulkCreateAssetsHarness harness)
    {
        var asset = new Asset
        {
            TenantId = harness.TenantProvider.TenantId!.Value,
            UnitId = harness.UnitId,
            CategoryId = harness.CategoryId,
            FamilyId = harness.FamilyId,
            Name = "Split",
            Tag = $"AC-{Guid.NewGuid():N}"[..12],
            Status = AssetStatus.Active,
        };
        harness.Db.Assets.Add(asset);
        await harness.Db.SaveChangesAsync();
        return asset;
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate vlr-api repository root.");
    }

    private sealed class RecordingGenerationService : IWorkOrderGenerationService
    {
        public int Calls { get; private set; }

        public Task<WorkOrderResponse> GenerateAsync(
            GenerateWorkOrderCommand command,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("generation must not be called while fail-closed");
        }
    }
}
