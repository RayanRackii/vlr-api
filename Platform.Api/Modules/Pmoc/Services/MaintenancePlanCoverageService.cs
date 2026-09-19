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
        IReadOnlyCollection<PmocOperationalStatus>? statuses,
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
        var lastDueDate = PmocDueCalendar.LastDueOnOrBefore(plan.Frequency, asOfDate);
        var nextDueDate = PmocDueCalendar.NextDueOnOrAfter(plan.Frequency, asOfDate);
        var isDueToday = PmocDueCalendar.IsDueToday(plan.Frequency, asOfDate);
        var wouldBeConsideredByGenerator =
            plan.IsActive && plan.AutoGenerateEnabled && isDueToday;

        var eligibleAssets = await dbContext.Assets
            .AsNoTracking()
            .Where(asset =>
                asset.UnitId == plan.UnitId
                && asset.CategoryId == plan.AssetCategoryId
                && asset.Status == AssetStatus.Active
                && asset.ScheduledDeletionAt == null)
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
        var filter = statuses is { Count: > 0 }
            ? statuses.ToHashSet()
            : null;

        var rows = new List<MaintenancePlanCoverageAssetItem>(eligibleAssets.Count);
        var neverExecuted = 0;
        var overdue = 0;
        var onTrack = 0;
        var withOpen = 0;

        foreach (var asset in eligibleAssets)
        {
            var relevant = workOrdersByAsset[asset.Id];
            var lastMaintenance = relevant
                .Where(workOrder => workOrder.Status == WorkOrderStatus.Completed)
                .OrderByDescending(workOrder => workOrder.CompletedDate ?? DateTimeOffset.MinValue)
                .ThenByDescending(workOrder => workOrder.ScheduledDate)
                .ThenByDescending(workOrder => workOrder.Id)
                .Select(workOrder => new MaintenancePlanLastMaintenance(
                    workOrder.Id,
                    workOrder.ScheduledDate,
                    workOrder.CompletedDate))
                .FirstOrDefault();

            var coversCurrentPeriod = relevant.Any(workOrder =>
                workOrder.Status == WorkOrderStatus.Completed
                && workOrder.ScheduledDate >= lastDueDate);

            var operationalStatus = lastMaintenance is null
                ? PmocOperationalStatus.NeverExecuted
                : coversCurrentPeriod
                    ? PmocOperationalStatus.OnTrack
                    : PmocOperationalStatus.Overdue;

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

            switch (operationalStatus)
            {
                case PmocOperationalStatus.NeverExecuted:
                    neverExecuted++;
                    break;
                case PmocOperationalStatus.Overdue:
                    overdue++;
                    break;
                case PmocOperationalStatus.OnTrack:
                    onTrack++;
                    break;
            }

            if (openWorkOrder is not null)
            {
                withOpen++;
            }

            if (filter is not null && !filter.Contains(operationalStatus))
            {
                continue;
            }

            rows.Add(new MaintenancePlanCoverageAssetItem(
                asset.Id,
                asset.Name,
                asset.Tag,
                lastMaintenance,
                nextDueDate,
                operationalStatus,
                openWorkOrder));
        }

        rows.Sort(CompareCoverageRows);

        var summary = new MaintenancePlanCoverageSummary(
            eligibleAssets.Count,
            overdue + onTrack,
            neverExecuted,
            overdue,
            onTrack,
            withOpen);

        return new MaintenancePlanCoverageResponse(
            plan.Id,
            asOfDate,
            plan.Frequency,
            lastDueDate,
            nextDueDate,
            plan.IsActive,
            plan.AutoGenerateEnabled,
            isDueToday,
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
        var status = Rank(left.OperationalStatus).CompareTo(Rank(right.OperationalStatus));
        if (status != 0)
        {
            return status;
        }

        var tag = string.Compare(left.Tag, right.Tag, StringComparison.OrdinalIgnoreCase);
        if (tag != 0)
        {
            return tag;
        }

        return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static int Rank(PmocOperationalStatus status) =>
        status switch
        {
            PmocOperationalStatus.Overdue => 0,
            PmocOperationalStatus.NeverExecuted => 1,
            PmocOperationalStatus.OnTrack => 2,
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
