using Microsoft.EntityFrameworkCore;
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
    int SkippedDuplicate);

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
                plan.IntervalDays,
                plan.FirstDueDate))
            .ToListAsync(cancellationToken);

        var reports = new List<PmocEnginePlanReport>(plans.Count);
        foreach (var plan in plans)
        {
            reports.Add(await ProcessPlanAsync(plan, asOfDate, cancellationToken));
        }

        logger.LogInformation(
            "PmocEngineJob finished for {AsOfDate}. PlansProcessed={PlansProcessed} Created={Created}.",
            asOfDate,
            reports.Count,
            reports.Sum(report => report.Created));

        return new PmocEngineRunReport(asOfDate, reports);
    }

    private async Task<PmocEnginePlanReport> ProcessPlanAsync(
        CandidatePlan plan,
        DateOnly asOfDate,
        CancellationToken cancellationToken)
    {
        var assets = await PmocAssetEligibility.WhereEligible(
                dbContext.Assets.IgnoreQueryFilters().AsNoTracking(),
                plan.TenantId,
                plan.UnitId,
                plan.CategoryId)
            .Select(asset => asset.Id)
            .ToListAsync(cancellationToken);

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
        var candidates = new List<Guid>();

        foreach (var assetId in assets)
        {
            var relevant = workOrdersByAsset[assetId].ToList();
            var due = PmocDueCalculator.Compute(
                new PmocDueInput(
                    plan.IntervalDays,
                    plan.FirstDueDate,
                    asOfDate,
                    PmocDueCalculator.PickLastCompleted(
                        relevant.Select(workOrder => (
                            workOrder.Id,
                            workOrder.Status,
                            workOrder.ScheduledDate,
                            workOrder.CompletedDate)))));

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
                    plan.Id,
                    assetId,
                    asOfDate,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "PmocEngineJob failed while generating plan {PlanId} asset {AssetId}.",
                    plan.Id,
                    assetId);
                throw;
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
            skippedDuplicate);

        logger.LogInformation(
            "PmocEngineJob plan {PlanId} schedule=interval eligible={EligibleAssets} due={DueAssets} created={Created} skippedNotDue={SkippedNotDue} skippedOpen={SkippedOpenWorkOrder} skippedRevalidation={SkippedRevalidation} skippedDuplicate={SkippedDuplicate}.",
            report.PlanId,
            report.EligibleAssets,
            report.DueAssets,
            report.Created,
            report.SkippedNotDue,
            report.SkippedOpenWorkOrder,
            report.SkippedRevalidation,
            report.SkippedDuplicate);

        return report;
    }

    private sealed record CandidatePlan(
        Guid Id,
        Guid TenantId,
        Guid UnitId,
        Guid CategoryId,
        int IntervalDays,
        DateOnly FirstDueDate);

    private sealed record CandidateWorkOrder(
        Guid Id,
        Guid AssetId,
        WorkOrderStatus Status,
        DateOnly ScheduledDate,
        DateTimeOffset? CompletedDate);
}
