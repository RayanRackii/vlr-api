using Microsoft.EntityFrameworkCore;
using Platform.Api.Services.Tenancy;
using Platform.Api.Modules.WorkOrders.Services;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Jobs;

public sealed record PmocEnginePlanReport(
    Guid PlanId,
    int EligibleAssets,
    int DueAssets,
    int Created,
    int SkippedNotDue,
    int SkippedOpenWorkOrder,
    int SkippedRevalidation,
    int SkippedDuplicate,
    int Failed = 0);

public sealed record PmocEngineRunReport(
    DateOnly AsOfDate,
    IReadOnlyList<PmocEnginePlanReport> Plans);

public sealed class PmocEngineJob(
    AppDbContext dbContext,
    IWorkOrderGenerationService workOrderGenerationService,
    TimeProvider timeProvider,
    ILogger<PmocEngineJob> logger)
{
    public Task ExecuteAsync(CancellationToken cancellationToken) =>
        RunAsync(cancellationToken);

    public async Task<PmocEngineRunReport> RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var _ = PlatformScope.Begin(logger, "pmoc-engine-sweep");
        var asOfDate = BrazilTimeZone.GetToday(timeProvider);

        var plans = await dbContext.MaintenancePlans
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(plan => plan.IsActive && plan.AutoGenerateEnabled)
            .Select(plan => new CandidatePlan(
                plan.Id,
                plan.TenantId,
                plan.UnitId,
                plan.AssetCategoryId,
                plan.AssetId,
                plan.IntervalDays,
                plan.FirstDueDate))
            .ToListAsync(cancellationToken);

        var reports = new List<PmocEnginePlanReport>(plans.Count);
        var now = timeProvider.GetUtcNow();
        foreach (var tenantPlans in plans.GroupBy(plan => plan.TenantId))
        {
            // One tenant or plan that throws must not stop the sweep for the others: Hangfire would retry
            // the whole job, fail at the same row, and starve every tenant sorted after it.
            TimeZoneInfo zone;
            DateOnly tenantDate;
            try
            {
                zone = await TenantTimeZoneProvider.ResolveForTenantAsync(dbContext, tenantPlans.Key, cancellationToken);
                tenantDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(
                    exception,
                    "PmocEngineJob failed to resolve the time zone for tenant {TenantId}; its plans were skipped.",
                    tenantPlans.Key);
                foreach (var plan in tenantPlans)
                {
                    reports.Add(FailedReport(plan.Id));
                }
                continue;
            }

            foreach (var plan in tenantPlans)
            {
                try
                {
                    reports.Add(await ProcessPlanAsync(plan, tenantDate, zone, cancellationToken));
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    dbContext.ChangeTracker.Clear();
                    logger.LogError(
                        exception,
                        "PmocEngineJob failed for tenant {TenantId} plan {PlanId}; continuing with the next plan.",
                        plan.TenantId,
                        plan.Id);
                    reports.Add(FailedReport(plan.Id));
                }
            }
        }

        logger.LogInformation(
            "PmocEngineJob finished for {AsOfDate}. PlansProcessed={PlansProcessed} Created={Created} Failed={Failed}.",
            asOfDate,
            reports.Count,
            reports.Sum(report => report.Created),
            reports.Sum(report => report.Failed));

        return new PmocEngineRunReport(asOfDate, reports);
    }

    private static PmocEnginePlanReport FailedReport(Guid planId) =>
        new(planId, 0, 0, 0, 0, 0, 0, 0, 1);

    private async Task<PmocEnginePlanReport> ProcessPlanAsync(
        CandidatePlan plan,
        DateOnly asOfDate,
        TimeZoneInfo zone,
        CancellationToken cancellationToken)
    {
        var assets = await PmocAssetEligibility.WhereEligible(
                dbContext.Assets.IgnoreQueryFilters().AsNoTracking(),
                plan.TenantId,
                plan.UnitId,
                plan.CategoryId,
                plan.AssetId)
            .Select(asset => asset.Id)
            .ToListAsync(cancellationToken);
        if (plan.AssetId is null)
        {
            var specific = await dbContext.MaintenancePlans
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(item => item.TenantId == plan.TenantId
                    && item.UnitId == plan.UnitId
                    && item.IsActive
                    && item.AssetId != null)
                .Select(item => item.AssetId!.Value)
                .ToListAsync(cancellationToken);
            var covered = specific.ToHashSet();
            assets = assets.Where(id => !covered.Contains(id)).ToList();
        }

        var workOrders = await dbContext.WorkOrders
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(workOrder =>
                workOrder.TenantId == plan.TenantId
                && workOrder.MaintenancePlanId == plan.Id
                && workOrder.Status != WorkOrderStatus.Canceled)
            .Select(workOrder => new CandidateWorkOrder(
                workOrder.Id,
                workOrder.AssetId,
                workOrder.Status,
                workOrder.ScheduledDate,
                workOrder.CompletedDate))
            .ToListAsync(cancellationToken);

        var workOrdersByAsset = workOrders.ToLookup(workOrder => workOrder.AssetId);
        var skippedNotDue = 0;
        var skippedOpen = 0;
        var dueAssets = 0;
        var failed = 0;
        var candidates = new List<Guid>();

        foreach (var assetId in assets)
        {
            var relevant = workOrdersByAsset[assetId].ToList();
            PmocDueResult due;
            try
            {
                due = PmocDueCalculator.Compute(
                    new PmocDueInput(
                        plan.IntervalDays,
                        plan.FirstDueDate,
                        asOfDate,
                        PmocDueCalculator.PickLastCompleted(
                            relevant.Select(workOrder => (
                                workOrder.Id,
                                workOrder.Status,
                                workOrder.ScheduledDate,
                                workOrder.CompletedDate)))), zone);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                // Bad plan or asset data (e.g. invalid interval, Completed work order without a date).
                failed++;
                logger.LogError(
                    exception,
                    "PmocEngineJob could not compute the due date for tenant {TenantId} plan {PlanId} asset {AssetId}; skipping the asset.",
                    plan.TenantId,
                    plan.Id,
                    assetId);
                continue;
            }

            if (due.DueStatus == PmocDueStatus.NotDue)
            {
                skippedNotDue++;
                continue;
            }

            dueAssets++;
            if (relevant.Any(workOrder =>
                    workOrder.Status is WorkOrderStatus.Pending or WorkOrderStatus.InProgress))
            {
                skippedOpen++;
                continue;
            }

            candidates.Add(assetId);
        }

        var created = 0;
        var skippedRevalidation = 0;
        var skippedDuplicate = 0;

        foreach (var assetId in candidates)
        {
            PmocAutomaticGenerationResult result;
            try
            {
                result = await workOrderGenerationService.TryGenerateAutomaticAsync(
                    plan.TenantId,
                    plan.Id,
                    assetId,
                    asOfDate,
                    cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                dbContext.ChangeTracker.Clear();
                failed++;
                logger.LogError(
                    exception,
                    "PmocEngineJob failed while generating tenant {TenantId} plan {PlanId} asset {AssetId}; continuing.",
                    plan.TenantId,
                    plan.Id,
                    assetId);
                continue;
            }

            switch (result.Outcome)
            {
                case PmocAutomaticGenerationOutcome.Created:
                    created++;
                    break;
                case PmocAutomaticGenerationOutcome.SkippedNotDue:
                    skippedNotDue++;
                    break;
                case PmocAutomaticGenerationOutcome.SkippedOpenWorkOrder:
                    skippedOpen++;
                    break;
                case PmocAutomaticGenerationOutcome.SkippedRevalidation:
                    skippedRevalidation++;
                    break;
                case PmocAutomaticGenerationOutcome.SkippedDuplicate:
                    skippedDuplicate++;
                    break;
            }
        }

        var report = new PmocEnginePlanReport(
            plan.Id,
            assets.Count,
            dueAssets,
            created,
            skippedNotDue,
            skippedOpen,
            skippedRevalidation,
            skippedDuplicate,
            failed);

        logger.LogInformation(
            "PmocEngineJob plan {PlanId} schedule=interval eligible={EligibleAssets} due={DueAssets} created={Created} skippedNotDue={SkippedNotDue} skippedOpen={SkippedOpenWorkOrder} skippedRevalidation={SkippedRevalidation} skippedDuplicate={SkippedDuplicate} failed={Failed}.",
            report.PlanId,
            report.EligibleAssets,
            report.DueAssets,
            report.Created,
            report.SkippedNotDue,
            report.SkippedOpenWorkOrder,
            report.SkippedRevalidation,
            report.SkippedDuplicate,
            report.Failed);

        return report;
    }

    private sealed record CandidatePlan(
        Guid Id,
        Guid TenantId,
        Guid UnitId,
        Guid? CategoryId,
        Guid? AssetId,
        int IntervalDays,
        DateOnly FirstDueDate);

    private sealed record CandidateWorkOrder(
        Guid Id,
        Guid AssetId,
        WorkOrderStatus Status,
        DateOnly ScheduledDate,
        DateTimeOffset? CompletedDate);
}
