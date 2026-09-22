using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Authorization;
using Platform.Api.Jobs;
using Platform.Api.Modules.Users.Services;
using Platform.Api.Modules.WorkOrders.Dtos;
using Platform.Api.Modules.WorkOrders.Services;
using Platform.Api.Notifications;
using Platform.Api.Tests.Assets;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.WorkOrders;

public sealed class PmocEngineJobTests
{
    private static readonly DateOnly AsOf = new(2026, 9, 19);

    [Fact]
    public async Task A_inactive_plan_generates_zero()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, isActive: false, firstDueDate: AsOf.AddDays(-10));
        await AddAssetAsync(harness, "A");

        var report = await RunAsync(harness);

        Assert.DoesNotContain(report.Plans, item => item.PlanId == plan.Id);
        Assert.Empty(await harness.Db.WorkOrders.ToListAsync());
    }

    [Fact]
    public async Task B_auto_disabled_generates_zero()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        await SeedPlanAsync(harness, autoGenerateEnabled: false, firstDueDate: AsOf.AddDays(-10));
        await AddAssetAsync(harness, "B");

        var report = await RunAsync(harness);

        Assert.Empty(report.Plans);
        Assert.Empty(await harness.Db.WorkOrders.ToListAsync());
    }

    [Fact]
    public async Task C_no_eligible_assets_generates_zero()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: AsOf);

        var report = await RunAsync(harness);

        var row = Assert.Single(report.Plans);
        Assert.Equal(plan.Id, row.PlanId);
        Assert.Equal(0, row.EligibleAssets);
        Assert.Equal(0, row.Created);
    }

    [Fact]
    public async Task D_not_due_generates_zero()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: AsOf.AddDays(10));
        await AddAssetAsync(harness, "D");

        var report = await RunAsync(harness);

        var row = Assert.Single(report.Plans);
        Assert.Equal(0, row.Created);
        Assert.Equal(1, row.SkippedNotDue);
        Assert.Equal(0, row.DueAssets);
        Assert.Empty(await harness.Db.WorkOrders.ToListAsync());
    }

    [Fact]
    public async Task E_due_today_generates_on_the_calculated_date()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: AsOf);
        var asset = await AddAssetAsync(harness, "E");

        var report = await RunAsync(harness);

        var created = Assert.Single(await harness.Db.WorkOrders.ToListAsync());
        Assert.Equal(1, Assert.Single(report.Plans).Created);
        Assert.Equal(asset.Id, created.AssetId);
        Assert.Equal(AsOf, created.ScheduledDate);
        Assert.Equal(WorkOrderStatus.Pending, created.Status);
        Assert.Null(created.AssignedUserId);
    }

    [Fact]
    public async Task F_overdue_generates_one_work_order()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        await SeedPlanAsync(harness, firstDueDate: new DateOnly(2026, 9, 1));
        await AddAssetAsync(harness, "F");

        await RunAsync(harness);

        Assert.Single(await harness.Db.WorkOrders.ToListAsync());
    }

    [Fact]
    public async Task GJ_never_executed_past_first_due_uses_that_date_not_today()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        await SeedPlanAsync(harness, firstDueDate: new DateOnly(2026, 9, 1));
        await AddAssetAsync(harness, "GJ");

        await RunAsync(harness);

        var created = Assert.Single(await harness.Db.WorkOrders.ToListAsync());
        Assert.Equal(new DateOnly(2026, 9, 1), created.ScheduledDate);
        Assert.NotEqual(AsOf, created.ScheduledDate);
    }

    [Fact]
    public async Task H_executed_overdue_uses_completed_civil_date_plus_interval()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, intervalDays: 30, firstDueDate: new DateOnly(2026, 1, 1));
        var asset = await AddAssetAsync(harness, "H");
        await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.Completed,
            scheduledDate: new DateOnly(2026, 8, 1),
            completedDate: BrazilTimeZone.AtLocal(new DateOnly(2026, 8, 2), new TimeOnly(12, 0)));

        await RunAsync(harness);

        var created = Assert.Single(
            await harness.Db.WorkOrders.Where(item => item.Status == WorkOrderStatus.Pending).ToListAsync());
        Assert.Equal(new DateOnly(2026, 9, 1), created.ScheduledDate);
    }

    [Fact]
    public async Task KAE_assets_are_independent_and_counters_match()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var logger = new CollectingLogger<PmocEngineJob>();
        var plan = await SeedPlanAsync(harness, intervalDays: 30, firstDueDate: new DateOnly(2026, 9, 1));
        var create = await AddAssetAsync(harness, "A-NEW");
        var dueToday = await AddAssetAsync(harness, "B-TODAY");
        var notDue = await AddAssetAsync(harness, "C-OK");
        var open = await AddAssetAsync(harness, "D-OPEN");
        await AddWorkOrderAsync(
            harness,
            plan.Id,
            dueToday.Id,
            WorkOrderStatus.Completed,
            scheduledDate: new DateOnly(2026, 8, 19),
            completedDate: BrazilTimeZone.AtLocal(new DateOnly(2026, 8, 20), new TimeOnly(12, 0)));
        await AddWorkOrderAsync(
            harness,
            plan.Id,
            notDue.Id,
            WorkOrderStatus.Completed,
            scheduledDate: new DateOnly(2026, 9, 1),
            completedDate: BrazilTimeZone.AtLocal(new DateOnly(2026, 9, 2), new TimeOnly(12, 0)));
        await AddWorkOrderAsync(
            harness,
            plan.Id,
            open.Id,
            WorkOrderStatus.Pending,
            scheduledDate: new DateOnly(2026, 9, 1));

        var report = await RunAsync(harness, logger);

        var row = Assert.Single(report.Plans);
        Assert.Equal(4, row.EligibleAssets);
        Assert.Equal(3, row.DueAssets);
        Assert.Equal(2, row.Created);
        Assert.Equal(1, row.SkippedNotDue);
        Assert.Equal(1, row.SkippedOpenWorkOrder);
        Assert.Equal(0, row.SkippedRevalidation);
        Assert.Equal(0, row.SkippedDuplicate);
        var pending = await harness.Db.WorkOrders.Where(item => item.Status == WorkOrderStatus.Pending).ToListAsync();
        Assert.Equal(3, pending.Count);
        Assert.Contains(pending, item => item.AssetId == create.Id && item.ScheduledDate == new DateOnly(2026, 9, 1));
        Assert.Contains(pending, item => item.AssetId == dueToday.Id && item.ScheduledDate == AsOf);
        Assert.DoesNotContain(pending, item => item.AssetId == notDue.Id);
        Assert.Single(pending, item => item.AssetId == open.Id);
        Assert.Contains(logger.Messages, message => message.Contains("skippedOpen=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task L_pending_open_skips_without_throwing()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: new DateOnly(2026, 9, 1));
        var asset = await AddAssetAsync(harness, "L");
        await AddWorkOrderAsync(harness, plan.Id, asset.Id, WorkOrderStatus.Pending, AsOf);

        var report = await RunAsync(harness);

        Assert.Equal(1, Assert.Single(report.Plans).SkippedOpenWorkOrder);
        Assert.Equal(0, report.Plans[0].Created);
        Assert.Single(await harness.Db.WorkOrders.ToListAsync());
    }

    [Fact]
    public async Task M_in_progress_open_skips()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: new DateOnly(2026, 9, 1));
        var asset = await AddAssetAsync(harness, "M");
        await AddWorkOrderAsync(harness, plan.Id, asset.Id, WorkOrderStatus.InProgress, AsOf);

        var report = await RunAsync(harness);

        Assert.Equal(1, Assert.Single(report.Plans).SkippedOpenWorkOrder);
        Assert.Equal(0, report.Plans[0].Created);
    }

    [Fact]
    public async Task N_canceled_does_not_block_the_next_automatic_run()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: new DateOnly(2026, 9, 1));
        var asset = await AddAssetAsync(harness, "N");
        await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.Canceled,
            scheduledDate: new DateOnly(2026, 9, 1));

        await RunAsync(harness);

        var created = Assert.Single(
            await harness.Db.WorkOrders.Where(item => item.Status == WorkOrderStatus.Pending).ToListAsync());
        Assert.Equal(new DateOnly(2026, 9, 1), created.ScheduledDate);
    }

    [Fact]
    public async Task O_second_run_does_not_duplicate()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        await SeedPlanAsync(harness, firstDueDate: new DateOnly(2026, 9, 1));
        await AddAssetAsync(harness, "O");

        await RunAsync(harness);
        var second = await RunAsync(harness);

        Assert.Equal(0, Assert.Single(second.Plans).Created);
        Assert.Equal(1, second.Plans[0].SkippedOpenWorkOrder);
        Assert.Single(await harness.Db.WorkOrders.ToListAsync());
    }

    [Fact]
    public async Task Q_latest_completion_moves_the_next_due_out_of_range()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, intervalDays: 30, firstDueDate: new DateOnly(2026, 1, 1));
        var asset = await AddAssetAsync(harness, "Q");
        await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.Completed,
            scheduledDate: new DateOnly(2026, 7, 1),
            completedDate: BrazilTimeZone.AtLocal(new DateOnly(2026, 7, 1), new TimeOnly(12, 0)));
        await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.Completed,
            scheduledDate: new DateOnly(2026, 9, 1),
            completedDate: BrazilTimeZone.AtLocal(new DateOnly(2026, 9, 10), new TimeOnly(12, 0)));

        var report = await RunAsync(harness);

        Assert.Equal(0, Assert.Single(report.Plans).Created);
        Assert.Equal(1, report.Plans[0].SkippedNotDue);
    }

    [Fact]
    public async Task R_early_manual_completion_prevents_stale_due_generation()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var asOf = new DateOnly(2026, 10, 20);
        var plan = await SeedPlanAsync(harness, intervalDays: 30, firstDueDate: asOf);
        var asset = await AddAssetAsync(harness, "R");
        var manual = await CreateGenerator(harness).GenerateAsync(
            new GenerateWorkOrderCommand(plan.Id, asset.Id, new DateOnly(2026, 10, 12), null),
            CancellationToken.None);
        await CreateStatusService(harness).UpdateStatusAsync(
            manual.Id,
            new UpdateWorkOrderStatusRequest { Status = WorkOrderStatus.Completed },
            CancellationToken.None);

        var report = await RunAsync(harness, asOf: asOf);

        Assert.Equal(0, Assert.Single(report.Plans).Created);
        Assert.DoesNotContain(
            await harness.Db.WorkOrders.ToListAsync(),
            item => item.Status == WorkOrderStatus.Pending && item.ScheduledDate == asOf);
    }

    [Fact]
    public async Task S_pending_does_not_reset_the_cycle()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: new DateOnly(2026, 9, 1));
        var asset = await AddAssetAsync(harness, "S");
        var pending = await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.Pending,
            scheduledDate: AsOf);
        pending.Status = WorkOrderStatus.Canceled;
        await harness.Db.SaveChangesAsync();

        await RunAsync(harness);

        var created = Assert.Single(
            await harness.Db.WorkOrders.Where(item => item.Status == WorkOrderStatus.Pending).ToListAsync());
        Assert.Equal(new DateOnly(2026, 9, 1), created.ScheduledDate);
    }

    [Fact]
    public async Task T_in_progress_does_not_reset_the_cycle()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: new DateOnly(2026, 9, 1));
        var asset = await AddAssetAsync(harness, "T");
        var open = await AddWorkOrderAsync(
            harness,
            plan.Id,
            asset.Id,
            WorkOrderStatus.InProgress,
            scheduledDate: AsOf);
        open.Status = WorkOrderStatus.Canceled;
        await harness.Db.SaveChangesAsync();

        await RunAsync(harness);

        var created = Assert.Single(
            await harness.Db.WorkOrders.Where(item => item.Status == WorkOrderStatus.Pending).ToListAsync());
        Assert.Equal(new DateOnly(2026, 9, 1), created.ScheduledDate);
    }

    [Fact]
    public async Task U_completed_resets_the_cycle()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        await SeedPlanAsync(harness, intervalDays: 30, firstDueDate: new DateOnly(2026, 9, 1));
        await AddAssetAsync(harness, "U");
        await RunAsync(harness);
        var generated = Assert.Single(await harness.Db.WorkOrders.ToListAsync());
        await CreateStatusService(harness).UpdateStatusAsync(
            generated.Id,
            new UpdateWorkOrderStatusRequest { Status = WorkOrderStatus.Completed },
            CancellationToken.None);

        var second = await RunAsync(harness);

        Assert.Equal(0, Assert.Single(second.Plans).Created);
        Assert.Equal(1, second.Plans[0].SkippedNotDue);
        Assert.Equal(
            1,
            await harness.Db.WorkOrders.CountAsync(item => item.Status == WorkOrderStatus.Completed));
    }

    [Fact]
    public async Task V_manual_unlinked_work_order_does_not_affect_generation()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        await SeedPlanAsync(harness, firstDueDate: new DateOnly(2026, 9, 1));
        var asset = await AddAssetAsync(harness, "V");
        await AddWorkOrderAsync(
            harness,
            maintenancePlanId: null,
            asset.Id,
            WorkOrderStatus.Completed,
            scheduledDate: new DateOnly(2026, 9, 2),
            completedDate: BrazilTimeZone.AtLocal(AsOf, new TimeOnly(12, 0)));

        await RunAsync(harness);

        var created = Assert.Single(
            await harness.Db.WorkOrders.Where(item => item.MaintenancePlanId != null).ToListAsync());
        Assert.Equal(new DateOnly(2026, 9, 1), created.ScheduledDate);
    }

    [Fact]
    public async Task W_plan_edit_revalidation_skips_when_no_longer_due()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: new DateOnly(2026, 9, 1));
        var asset = await AddAssetAsync(harness, "W");
        plan.FirstDueDate = AsOf.AddMonths(1);
        await harness.Db.SaveChangesAsync();

        var result = await CreateGenerator(harness).TryGenerateAutomaticAsync(
            plan.Id,
            asset.Id,
            AsOf,
            CancellationToken.None);

        Assert.Equal(PmocAutomaticGenerationOutcome.SkippedNotDue, result.Outcome);
        Assert.Empty(await harness.Db.WorkOrders.ToListAsync());
    }

    [Fact]
    public async Task X_eligibility_revalidation_skips_inactive_asset()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: new DateOnly(2026, 9, 1));
        var asset = await AddAssetAsync(harness, "X");
        asset.Status = AssetStatus.Inactive;
        await harness.Db.SaveChangesAsync();

        var result = await CreateGenerator(harness).TryGenerateAutomaticAsync(
            plan.Id,
            asset.Id,
            AsOf,
            CancellationToken.None);

        Assert.Equal(PmocAutomaticGenerationOutcome.SkippedRevalidation, result.Outcome);
        Assert.Empty(await harness.Db.WorkOrders.ToListAsync());
    }

    [Fact]
    public async Task YZAA_writer_reuses_calculator_and_snapshots_tasks()
    {
        var jobSource = File.ReadAllText(FindRepoFile(Path.Combine("Platform.Api", "Jobs", "PmocEngineJob.cs")));
        var writerSource = File.ReadAllText(FindRepoFile(
            Path.Combine("Platform.Api", "Modules", "WorkOrders", "Services", "WorkOrderGenerationService.cs")));
        Assert.Contains("PmocDueCalculator.Compute", jobSource, StringComparison.Ordinal);
        Assert.Contains("PmocDueCalculator.Compute", writerSource, StringComparison.Ordinal);
        Assert.DoesNotContain(".AddDays(", jobSource, StringComparison.Ordinal);
        Assert.DoesNotContain(".AddDays(", writerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new WorkOrder", jobSource, StringComparison.Ordinal);
        Assert.Contains("InsertSnapshotAsync", writerSource, StringComparison.Ordinal);

        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: AsOf, name: "Snapshot plan");
        await AddAssetAsync(harness, "AA");

        await RunAsync(harness);

        var created = Assert.Single(await harness.Db.WorkOrders.Include(item => item.Tasks).ToListAsync());
        Assert.Equal("Snapshot plan", created.SourcePlanName);
        Assert.Equal(plan.Id, created.MaintenancePlanId);
        var task = Assert.Single(created.Tasks);
        Assert.Equal("Filtro", task.Title);
        Assert.Null(task.Value);
        Assert.Null(created.AssignedUserId);
    }

    [Fact]
    public async Task AB_tenant_isolation_and_explicit_tenant_match()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: AsOf);
        await AddAssetAsync(harness, "AB");
        var foreign = await AddAssetAsync(harness, "FOREIGN");
        foreign.TenantId = Guid.NewGuid();
        await harness.Db.SaveChangesAsync();

        var tenantB = new Tenant("Club B", "88888888000193", subdomain: "pmoc-job-b");
        var unitB = new Unit(tenantB.Id, "Filial");
        var categoryB = new AssetCategory { TenantId = tenantB.Id, Name = "AC" };
        harness.Db.Tenants.Add(tenantB);
        harness.Db.Units.Add(unitB);
        harness.Db.AssetCategories.Add(categoryB);
        var planB = new MaintenancePlan
        {
            TenantId = tenantB.Id,
            UnitId = unitB.Id,
            Name = "Tenant B",
            IntervalDays = 30,
            FirstDueDate = AsOf,
            AssetCategoryId = categoryB.Id,
            IsActive = true,
            AutoGenerateEnabled = true,
        };
        planB.AddTask(TaskFor(tenantB.Id, planB.Id));
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

        var report = await RunAsync(harness);

        Assert.Equal(2, report.Plans.Count);
        Assert.Contains(report.Plans, item => item.PlanId == plan.Id && item.EligibleAssets == 1 && item.Created == 1);
        Assert.Contains(report.Plans, item => item.PlanId == planB.Id && item.EligibleAssets == 1 && item.Created == 1);
        var orders = await harness.Db.WorkOrders.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(2, orders.Count);
        Assert.DoesNotContain(orders, item => item.AssetId == foreign.Id);
    }

    [Fact]
    public async Task AD_open_skip_is_not_an_exception()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: AsOf);
        var asset = await AddAssetAsync(harness, "AD");
        await AddWorkOrderAsync(harness, plan.Id, asset.Id, WorkOrderStatus.Pending, AsOf);

        var exception = await Record.ExceptionAsync(() => RunAsync(harness));

        Assert.Null(exception);
    }

    [Fact]
    public async Task AG_manual_from_plan_still_creates_while_another_pmoc_work_order_is_open()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var plan = await SeedPlanAsync(harness, firstDueDate: AsOf);
        var asset = await AddAssetAsync(harness, "AG");
        await AddWorkOrderAsync(harness, plan.Id, asset.Id, WorkOrderStatus.Pending, AsOf);

        var manual = await CreateGenerator(harness).GenerateAsync(
            new GenerateWorkOrderCommand(plan.Id, asset.Id, AsOf.AddDays(1), null),
            CancellationToken.None);

        Assert.Equal(WorkOrderStatus.Pending, manual.Status);
        Assert.Equal(2, await harness.Db.WorkOrders.CountAsync());
    }

    [Fact]
    public async Task Requires_maintenance_false_still_generates()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        await SeedPlanAsync(harness, firstDueDate: AsOf);
        var asset = await AddAssetAsync(harness, "FLAG", requiresMaintenance: false);

        await RunAsync(harness);

        Assert.Equal(asset.Id, Assert.Single(await harness.Db.WorkOrders.ToListAsync()).AssetId);
    }

    [Fact]
    public void Hangfire_schedule_remains_once_per_brazil_civil_day()
    {
        var hangfire = File.ReadAllText(FindRepoFile(Path.Combine("Platform.Api", "Jobs", "HangfireExtensions.cs")));
        Assert.Contains("PmocEngineJobId = \"pmoc-engine\"", hangfire, StringComparison.Ordinal);
        Assert.Contains("\"0 6 * * *\"", hangfire, StringComparison.Ordinal);
        Assert.Contains("ResolveBrazilTimeZone()", hangfire, StringComparison.Ordinal);
        Assert.Contains("WorkerCount = 1", hangfire, StringComparison.Ordinal);
    }

    private static async Task<PmocEngineRunReport> RunAsync(
        BulkCreateAssetsHarness harness,
        ILogger<PmocEngineJob>? logger = null,
        DateOnly? asOf = null)
    {
        var clock = new TestTimeProvider(BrazilTimeZone.AtLocal(asOf ?? AsOf, new TimeOnly(12, 0)));
        harness.TenantProvider.TenantId = null;
        var report = await new PmocEngineJob(
                harness.Db,
                CreateGenerator(harness),
                clock,
                logger ?? NullLogger<PmocEngineJob>.Instance)
            .RunAsync(CancellationToken.None);
        harness.TenantProvider.TenantId = harness.Db.Tenants.Select(tenant => tenant.Id).First();
        return report;
    }

    private static WorkOrderGenerationService CreateGenerator(BulkCreateAssetsHarness harness) =>
        new(harness.Db, harness.TenantProvider, TestPermissionResolvers.Create(harness.Db, harness.TenantProvider));

    private static WorkOrderService CreateStatusService(BulkCreateAssetsHarness harness)
    {
        var http = new FakeHttpContextAccessor();
        var identity = new ClaimsIdentity(
            [new Claim("email", "ops@club.test"), new Claim("sub", "admin-sub")],
            authenticationType: "Test");
        http.HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        var resolver = TestPermissionResolvers.Create(harness.Db, harness.TenantProvider);
        var users = new UserDirectoryService(
            harness.Db,
            harness.TenantProvider,
            new FakePlatformAdminChecker("ops@club.test"),
            resolver,
            new RbacGrantGuard(harness.Db, resolver, NullLogger<RbacGrantGuard>.Instance),
            new FakeTrialGuard(),
            new NotificationQueue(),
            new ConfigurationBuilder().Build(),
            new FakeHostEnvironment(),
            NullLogger<UserDirectoryService>.Instance);
        return new WorkOrderService(
            harness.Db,
            harness.TenantProvider,
            http,
            users,
            resolver,
            harness.CreateRegistry());
    }

    private static async Task<MaintenancePlan> SeedPlanAsync(
        BulkCreateAssetsHarness harness,
        int intervalDays = 30,
        DateOnly? firstDueDate = null,
        bool isActive = true,
        bool autoGenerateEnabled = true,
        string name = "Interval plan")
    {
        var plan = new MaintenancePlan
        {
            TenantId = harness.TenantProvider.TenantId!.Value,
            UnitId = harness.UnitId,
            Name = name,
            IntervalDays = intervalDays,
            FirstDueDate = firstDueDate ?? AsOf,
            AssetCategoryId = harness.CategoryId,
            IsActive = isActive,
            AutoGenerateEnabled = autoGenerateEnabled,
        };
        plan.AddTask(TaskFor(plan.TenantId, plan.Id));
        harness.Db.MaintenancePlans.Add(plan);
        await harness.Db.SaveChangesAsync();
        return plan;
    }

    private static PlanTask TaskFor(Guid tenantId, Guid planId) =>
        new()
        {
            TenantId = tenantId,
            MaintenancePlanId = planId,
            Title = "Filtro",
            InputType = TaskInputType.Checkbox,
            IsMandatory = false,
            Order = 1,
        };

    private static async Task<Asset> AddAssetAsync(
        BulkCreateAssetsHarness harness,
        string tag,
        bool requiresMaintenance = false)
    {
        var asset = new Asset
        {
            TenantId = harness.TenantProvider.TenantId!.Value,
            UnitId = harness.UnitId,
            CategoryId = harness.CategoryId,
            FamilyId = harness.FamilyId,
            Name = $"Split {tag}",
            Tag = tag,
            Status = AssetStatus.Active,
            RequiresMaintenance = requiresMaintenance,
        };
        harness.Db.Assets.Add(asset);
        await harness.Db.SaveChangesAsync();
        return asset;
    }

    private static async Task<WorkOrder> AddWorkOrderAsync(
        BulkCreateAssetsHarness harness,
        Guid? maintenancePlanId,
        Guid assetId,
        WorkOrderStatus status,
        DateOnly scheduledDate,
        DateTimeOffset? completedDate = null)
    {
        var workOrder = new WorkOrder
        {
            TenantId = harness.TenantProvider.TenantId!.Value,
            AssetId = assetId,
            MaintenancePlanId = maintenancePlanId,
            Status = status,
            ScheduledDate = scheduledDate,
            CompletedDate = completedDate,
            SourcePlanName = maintenancePlanId is null ? null : "Interval plan",
        };
        harness.Db.WorkOrders.Add(workOrder);
        await harness.Db.SaveChangesAsync();
        return workOrder;
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

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
