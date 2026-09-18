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
    public async Task Job_skips_active_plan_when_auto_generate_is_off()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        await SeedDailyPlanWithAssetAsync(harness, autoGenerateEnabled: false, isActive: true);
        harness.TenantProvider.TenantId = null;

        await CreateJob(harness).ExecuteAsync(CancellationToken.None);

        Assert.Empty(await harness.Db.WorkOrders.ToListAsync());
    }

    [Fact]
    public async Task Job_creates_pending_snapshot_when_active_and_auto_generate_on()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var (plan, asset) = await SeedDailyPlanWithAssetAsync(
            harness,
            autoGenerateEnabled: true,
            isActive: true,
            planName: "Auto PMOC",
            extraTask: true);
        Assert.NotNull(asset);
        var tenantId = harness.TenantProvider.TenantId!.Value;
        harness.TenantProvider.TenantId = null;
        var today = HangfireExtensions.GetBrazilToday();

        await CreateJob(harness).ExecuteAsync(CancellationToken.None);

        var workOrder = Assert.Single(await harness.Db.WorkOrders.ToListAsync());
        Assert.Equal(WorkOrderStatus.Pending, workOrder.Status);
        Assert.Equal(plan.Id, workOrder.MaintenancePlanId);
        Assert.Equal("Auto PMOC", workOrder.SourcePlanName);
        Assert.Equal(tenantId, workOrder.TenantId);
        Assert.Equal(asset.Id, workOrder.AssetId);
        Assert.Null(workOrder.AssignedUserId);
        Assert.Null(workOrder.Notes);
        Assert.Equal(today, workOrder.ScheduledDate);
        var tasks = await harness.Db.WorkOrderTasks
            .Where(task => task.WorkOrderId == workOrder.Id)
            .OrderBy(task => task.Order)
            .ToListAsync();
        Assert.Equal(["Filtro", "Evaporadora"], tasks.Select(task => task.Title).ToArray());
        Assert.Equal(
            plan.Tasks.Select(task => task.Id).ToArray(),
            tasks.Select(task => task.PlanTaskId!.Value).ToArray());
        Assert.All(tasks, task => Assert.Null(task.Value));
    }

    [Fact]
    public async Task Job_skips_inactive_plan_even_when_auto_generate_on()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        await SeedDailyPlanWithAssetAsync(harness, autoGenerateEnabled: true, isActive: false);
        harness.TenantProvider.TenantId = null;

        await CreateJob(harness).ExecuteAsync(CancellationToken.None);

        Assert.Empty(await harness.Db.WorkOrders.ToListAsync());
    }

    [Fact]
    public async Task Job_skips_empty_tasks_without_creating_work_orders()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        await CreateMatchingAssetAsync(harness);
        var plan = new MaintenancePlan
        {
            TenantId = harness.TenantProvider.TenantId!.Value,
            UnitId = harness.UnitId,
            Name = "Empty auto",
            Frequency = MaintenanceFrequency.Daily,
            AssetCategoryId = harness.CategoryId,
            IsActive = true,
            AutoGenerateEnabled = true,
        };
        harness.Db.MaintenancePlans.Add(plan);
        await harness.Db.SaveChangesAsync();
        harness.TenantProvider.TenantId = null;

        await CreateJob(harness).ExecuteAsync(CancellationToken.None);

        Assert.Empty(await harness.Db.WorkOrders.ToListAsync());
    }

    [Fact]
    public async Task Job_duplicate_on_one_asset_does_not_block_the_other()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var (plan, assetA) = await SeedDailyPlanWithAssetAsync(harness, autoGenerateEnabled: true, isActive: true);
        Assert.NotNull(assetA);
        var assetB = await CreateMatchingAssetAsync(harness, "AC-B");
        var today = HangfireExtensions.GetBrazilToday();
        harness.Db.WorkOrders.Add(new WorkOrder
        {
            TenantId = plan.TenantId,
            AssetId = assetA.Id,
            MaintenancePlanId = plan.Id,
            Status = WorkOrderStatus.Pending,
            ScheduledDate = today,
            SourcePlanName = plan.Name,
        });
        await harness.Db.SaveChangesAsync();
        harness.TenantProvider.TenantId = null;

        await CreateJob(harness).ExecuteAsync(CancellationToken.None);

        var workOrders = await harness.Db.WorkOrders.ToListAsync();
        Assert.Equal(2, workOrders.Count);
        Assert.Contains(workOrders, item => item.AssetId == assetA.Id);
        Assert.Contains(workOrders, item => item.AssetId == assetB.Id && item.Status == WorkOrderStatus.Pending);
        Assert.Equal(1, workOrders.Count(item => item.AssetId == assetA.Id));
    }

    [Fact]
    public async Task Job_canceled_work_order_allows_regeneration()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var (plan, asset) = await SeedDailyPlanWithAssetAsync(harness, autoGenerateEnabled: true, isActive: true);
        Assert.NotNull(asset);
        var today = HangfireExtensions.GetBrazilToday();
        harness.Db.WorkOrders.Add(new WorkOrder
        {
            TenantId = plan.TenantId,
            AssetId = asset.Id,
            MaintenancePlanId = plan.Id,
            Status = WorkOrderStatus.Canceled,
            ScheduledDate = today,
            SourcePlanName = "Old",
        });
        await harness.Db.SaveChangesAsync();
        harness.TenantProvider.TenantId = null;

        await CreateJob(harness).ExecuteAsync(CancellationToken.None);

        var workOrders = await harness.Db.WorkOrders.ToListAsync();
        Assert.Equal(2, workOrders.Count);
        Assert.Contains(
            workOrders,
            item => item.Status == WorkOrderStatus.Pending && item.SourcePlanName == plan.Name);
    }

    [Fact]
    public async Task Job_ignores_ineligible_assets()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var (plan, _) = await SeedDailyPlanWithAssetAsync(
            harness,
            autoGenerateEnabled: true,
            isActive: true,
            createAsset: false);
        await CreateMatchingAssetAsync(harness, "INACT", AssetStatus.Inactive);
        var deleting = await CreateMatchingAssetAsync(harness, "DEL");
        deleting.ScheduledDeletionAt = DateTimeOffset.UtcNow;
        var otherCategory = new AssetCategory
        {
            TenantId = harness.TenantProvider.TenantId!.Value,
            Name = "Other",
        };
        harness.Db.AssetCategories.Add(otherCategory);
        await harness.Db.SaveChangesAsync();
        var mismatched = await CreateMatchingAssetAsync(harness, "MIS");
        mismatched.CategoryId = otherCategory.Id;
        await harness.Db.SaveChangesAsync();
        _ = plan;
        harness.TenantProvider.TenantId = null;

        await CreateJob(harness).ExecuteAsync(CancellationToken.None);

        Assert.Empty(await harness.Db.WorkOrders.ToListAsync());
    }

    [Fact]
    public async Task Job_with_gqf_off_creates_for_every_tenant()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var (planA, _) = await SeedDailyPlanWithAssetAsync(
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
            Frequency = MaintenanceFrequency.Daily,
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

        await CreateJob(harness).ExecuteAsync(CancellationToken.None);

        var workOrders = await harness.Db.WorkOrders.ToListAsync();
        Assert.Equal(2, workOrders.Count);
        Assert.Contains(workOrders, item => item.TenantId == planA.TenantId && item.MaintenancePlanId == planA.Id);
        Assert.Contains(workOrders, item => item.TenantId == tenantB.Id && item.MaintenancePlanId == planB.Id);
    }

    [Fact]
    public async Task Job_continues_when_one_plan_throws()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var (failing, _) = await SeedDailyPlanWithAssetAsync(
            harness,
            autoGenerateEnabled: true,
            isActive: true,
            planName: "Failing");
        var (ok, _) = await SeedDailyPlanWithAssetAsync(
            harness,
            autoGenerateEnabled: true,
            isActive: true,
            planName: "Healthy",
            createAsset: false);
        harness.TenantProvider.TenantId = null;
        var inner = CreateGenerator(harness);
        var job = new PmocEngineJob(
            harness.Db,
            new ThrowingOnPlanGenerationService(inner, failing.Id),
            NullLogger<PmocEngineJob>.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        var workOrders = await harness.Db.WorkOrders.ToListAsync();
        Assert.Single(workOrders);
        Assert.Equal(ok.Id, workOrders[0].MaintenancePlanId);
    }

    [Fact]
    public async Task Job_second_run_same_day_is_duplicate_noop()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        await SeedDailyPlanWithAssetAsync(harness, autoGenerateEnabled: true, isActive: true);
        harness.TenantProvider.TenantId = null;
        var job = CreateJob(harness);

        await job.ExecuteAsync(CancellationToken.None);
        await job.ExecuteAsync(CancellationToken.None);

        Assert.Single(await harness.Db.WorkOrders.ToListAsync());
    }

    [Fact]
    public void Job_filter_and_hangfire_registration_match_slice_4()
    {
        var jobSource = File.ReadAllText(FindRepoFile(Path.Combine("Platform.Api", "Jobs", "PmocEngineJob.cs")));
        Assert.Contains("plan.IsActive && plan.AutoGenerateEnabled", jobSource, StringComparison.Ordinal);
        Assert.Contains("IWorkOrderGenerationService", jobSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new WorkOrder", jobSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AddTask", jobSource, StringComparison.Ordinal);

        var hangfire = File.ReadAllText(FindRepoFile(Path.Combine("Platform.Api", "Jobs", "HangfireExtensions.cs")));
        Assert.Contains("PmocEngineJobId = \"pmoc-engine\"", hangfire, StringComparison.Ordinal);
        Assert.Contains("\"0 6 * * *\"", hangfire, StringComparison.Ordinal);
        Assert.Contains("WorkerCount = 1", hangfire, StringComparison.Ordinal);
        Assert.Contains("ResolveBrazilTimeZone()", hangfire, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MaintenanceFrequency.Daily, 2026, 9, 18, true)]
    [InlineData(MaintenanceFrequency.Weekly, 2026, 9, 14, true)]
    [InlineData(MaintenanceFrequency.Weekly, 2026, 9, 15, false)]
    [InlineData(MaintenanceFrequency.Weekly, 2026, 9, 18, false)]
    [InlineData(MaintenanceFrequency.Monthly, 2026, 9, 1, true)]
    [InlineData(MaintenanceFrequency.Monthly, 2026, 9, 2, false)]
    [InlineData(MaintenanceFrequency.Quarterly, 2026, 1, 1, true)]
    [InlineData(MaintenanceFrequency.Quarterly, 2026, 4, 1, true)]
    [InlineData(MaintenanceFrequency.Quarterly, 2026, 7, 1, true)]
    [InlineData(MaintenanceFrequency.Quarterly, 2026, 10, 1, true)]
    [InlineData(MaintenanceFrequency.Quarterly, 2026, 2, 1, false)]
    [InlineData(MaintenanceFrequency.Quarterly, 2026, 1, 2, false)]
    [InlineData(MaintenanceFrequency.Semiannual, 2026, 1, 1, true)]
    [InlineData(MaintenanceFrequency.Semiannual, 2026, 7, 1, true)]
    [InlineData(MaintenanceFrequency.Semiannual, 2026, 4, 1, false)]
    [InlineData(MaintenanceFrequency.Annual, 2026, 1, 1, true)]
    [InlineData(MaintenanceFrequency.Annual, 2026, 1, 2, false)]
    [InlineData(MaintenanceFrequency.Annual, 2026, 12, 1, false)]
    public void IsDueToday_matches_legacy_calendar(
        MaintenanceFrequency frequency,
        int year,
        int month,
        int day,
        bool expected)
    {
        Assert.Equal(expected, PmocDueCalendar.IsDueToday(frequency, new DateOnly(year, month, day)));
    }

    private static PmocEngineJob CreateJob(BulkCreateAssetsHarness harness) =>
        new(harness.Db, CreateGenerator(harness), NullLogger<PmocEngineJob>.Instance);

    private static WorkOrderGenerationService CreateGenerator(BulkCreateAssetsHarness harness) =>
        new(
            harness.Db,
            harness.TenantProvider,
            TestPermissionResolvers.Create(harness.Db, harness.TenantProvider));

    private static async Task<(MaintenancePlanResponse Plan, Asset? Asset)> SeedDailyPlanWithAssetAsync(
        BulkCreateAssetsHarness harness,
        bool autoGenerateEnabled,
        bool isActive,
        string planName = "Daily PMOC",
        bool extraTask = false,
        bool createAsset = true)
    {
        var tasks = new List<CreatePlanTaskDto>
        {
            new()
            {
                Title = "Filtro",
                InputType = TaskInputType.Checkbox,
                Order = 1,
            },
        };
        if (extraTask)
        {
            tasks.Add(new CreatePlanTaskDto
            {
                Title = "Evaporadora",
                InputType = TaskInputType.Number,
                Order = 2,
                Configuration = """{"min":1}""",
            });
        }

        var plan = await new MaintenancePlanService(
            harness.Db,
            harness.TenantProvider,
            harness.CreateRegistry()).CreatePlanWithTasksAsync(
            new CreateMaintenancePlanRequest
            {
                UnitId = harness.UnitId,
                Name = planName,
                Frequency = MaintenanceFrequency.Daily,
                AssetCategoryId = harness.CategoryId,
                IsActive = isActive,
                AutoGenerateEnabled = autoGenerateEnabled,
                Tasks = tasks,
            },
            CancellationToken.None);

        Asset? asset = null;
        if (createAsset)
        {
            asset = await CreateMatchingAssetAsync(harness);
        }

        return (plan, asset);
    }

    private static async Task<Asset> CreateMatchingAssetAsync(
        BulkCreateAssetsHarness harness,
        string? tag = null,
        AssetStatus status = AssetStatus.Active)
    {
        var asset = new Asset
        {
            TenantId = harness.TenantProvider.TenantId!.Value,
            UnitId = harness.UnitId,
            CategoryId = harness.CategoryId,
            FamilyId = harness.FamilyId,
            Name = "Split",
            Tag = tag ?? $"AC-{Guid.NewGuid():N}"[..12],
            Status = status,
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

    private sealed class ThrowingOnPlanGenerationService(
        IWorkOrderGenerationService inner,
        Guid failingPlanId) : IWorkOrderGenerationService
    {
        public Task<WorkOrderResponse> GenerateAsync(
            GenerateWorkOrderCommand command,
            CancellationToken cancellationToken)
        {
            if (command.PlanId == failingPlanId)
            {
                throw new InvalidOperationException("simulated plan failure");
            }

            return inner.GenerateAsync(command, cancellationToken);
        }
    }
}
