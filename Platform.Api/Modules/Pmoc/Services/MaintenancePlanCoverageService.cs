using Microsoft.EntityFrameworkCore;
using Platform.Api.Jobs;
using Platform.Api.Modules.Pmoc.Dtos;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Modules.Pmoc.Services;

public sealed class MaintenancePlanCoverageService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    TimeProvider timeProvider) : IMaintenancePlanCoverageService
{
    public async Task<MaintenancePlanCoverageResponse?> GetCoverageAsync(
        Guid planId,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var plan = await dbContext.MaintenancePlans
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == planId, cancellationToken);

        if (plan is null)
        {
            return null;
        }

        var asOfDate = BrazilTimeZone.GetToday(timeProvider);

        var eligibleAssets = await PmocAssetEligibility.WhereEligible(
                dbContext.Assets.AsNoTracking(),
                plan.TenantId,
                plan.UnitId,
                plan.AssetCategoryId)
            .Select(asset => new EligibleAssetRow(asset.Id, asset.Name, asset.Tag))
            .ToListAsync(cancellationToken);

        var workOrders = await dbContext.WorkOrders
            .AsNoTracking()
            .Where(workOrder =>
                workOrder.MaintenancePlanId == plan.Id
                && workOrder.Status != WorkOrderStatus.Canceled)
            .Select(workOrder => new RelevantWorkOrderRow(
                workOrder.Id,
                workOrder.AssetId,
                workOrder.Status,
                workOrder.ScheduledDate,
                workOrder.CompletedDate))
            .ToListAsync(cancellationToken);

        var workOrdersByAsset = workOrders.ToLookup(workOrder => workOrder.AssetId);

        var rows = new List<MaintenancePlanCoverageAssetItem>(eligibleAssets.Count);
        var neverExecuted = 0;
        var executed = 0;
        var notDue = 0;
        var dueToday = 0;
        var overdue = 0;
        var needingAttention = 0;
        var withOpen = 0;

        foreach (var asset in eligibleAssets)
        {
            var relevant = workOrdersByAsset[asset.Id].ToList();
            var lastCompleted = PmocDueCalculator.PickLastCompleted(
                relevant.Select(workOrder => (
                    workOrder.Id,
                    workOrder.Status,
                    workOrder.ScheduledDate,
                    workOrder.CompletedDate)));

            var due = PmocDueCalculator.Compute(
                new PmocDueInput(
                    plan.IntervalDays,
                    plan.FirstDueDate,
                    asOfDate,
                    lastCompleted));

            var lastMaintenance = lastCompleted is null
                ? null
                : new MaintenancePlanLastMaintenance(
                    lastCompleted.Id,
                    lastCompleted.ScheduledDate,
                    lastCompleted.CompletedDate);

            var openWorkOrder = relevant
                .Where(workOrder =>
                    workOrder.Status is WorkOrderStatus.Pending or WorkOrderStatus.InProgress)
                .OrderBy(workOrder => workOrder.Status == WorkOrderStatus.InProgress ? 0 : 1)
                .ThenBy(workOrder => workOrder.ScheduledDate)
                .ThenBy(workOrder => workOrder.Id)
                .Select(workOrder => new MaintenancePlanOpenWorkOrder(
                    workOrder.Id,
                    workOrder.Status,
                    workOrder.ScheduledDate))
                .FirstOrDefault();

            switch (due.HistoryStatus)
            {
                case PmocHistoryStatus.NeverExecuted:
                    neverExecuted++;
                    break;
                case PmocHistoryStatus.Executed:
                    executed++;
                    break;
            }

            switch (due.DueStatus)
            {
                case PmocDueStatus.NotDue:
                    notDue++;
                    break;
                case PmocDueStatus.DueToday:
                    dueToday++;
                    break;
                case PmocDueStatus.Overdue:
                    overdue++;
                    break;
            }

            if (due.NeedsAttention)
            {
                needingAttention++;
            }

            if (openWorkOrder is not null)
            {
                withOpen++;
            }

            rows.Add(new MaintenancePlanCoverageAssetItem(
                asset.Id,
                asset.Name,
                asset.Tag,
                due.HistoryStatus,
                lastMaintenance,
                due.NextDueDate,
                due.DueStatus,
                due.NeedsAttention,
                openWorkOrder));
        }

        rows.Sort(CompareCoverageRows);

        var summary = new MaintenancePlanCoverageSummary(
            eligibleAssets.Count,
            neverExecuted,
            executed,
            notDue,
            dueToday,
            overdue,
            needingAttention,
            withOpen);

        var wouldBeConsideredByGenerator =
            plan.IsActive
            && plan.AutoGenerateEnabled
            && needingAttention > 0;

        return new MaintenancePlanCoverageResponse(
            plan.Id,
            asOfDate,
            plan.IntervalDays,
            plan.FirstDueDate,
            plan.IsActive,
            plan.AutoGenerateEnabled,
            wouldBeConsideredByGenerator,
            summary,
            rows);
    }

    private Guid EnsureTenantContext()
    {
        return tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");
    }

    private static int CompareCoverageRows(
        MaintenancePlanCoverageAssetItem left,
        MaintenancePlanCoverageAssetItem right)
    {
        var due = Rank(left.DueStatus).CompareTo(Rank(right.DueStatus));
        if (due != 0)
        {
            return due;
        }

        var tag = string.Compare(left.Tag, right.Tag, StringComparison.OrdinalIgnoreCase);
        if (tag != 0)
        {
            return tag;
        }

        var name = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        if (name != 0)
        {
            return name;
        }

        return left.AssetId.CompareTo(right.AssetId);
    }

    private static int Rank(PmocDueStatus status) =>
        status switch
        {
            PmocDueStatus.Overdue => 0,
            PmocDueStatus.DueToday => 1,
            PmocDueStatus.NotDue => 2,
            _ => 3,
        };

    private sealed record EligibleAssetRow(Guid Id, string Name, string Tag);

    private sealed record RelevantWorkOrderRow(
        Guid Id,
        Guid AssetId,
        WorkOrderStatus Status,
        DateOnly ScheduledDate,
        DateTimeOffset? CompletedDate);
}
