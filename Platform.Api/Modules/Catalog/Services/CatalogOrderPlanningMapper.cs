using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Catalog.Services;

/// <summary>
/// Composes the Phase 0 planning fields (per-item production summary, order-level
/// <c>estimatedReadyAt</c>, <c>orderVersion</c>/<c>planningRevision</c>) onto the base
/// <see cref="CatalogOrderResponse"/> shape built by <see cref="CatalogOrderService.MapOrder"/>.
/// Centralized here so staff reads, the portal and the approve/usage endpoints all compute the
/// same production-summary rules from the same two small tables, without re-querying per order.
/// </summary>
internal static class CatalogOrderPlanningMapper
{
    /// <summary>
    /// Loads order-item resource requirement snapshots and their current allocation (if any) for a
    /// batch of orders, then rebuilds each <see cref="CatalogOrderResponse"/> with planning fields
    /// populated. <paramref name="includeAllocationDetail"/> controls whether the per-item
    /// <see cref="CatalogOrderItemAllocationResponse"/> (which carries <c>RentalAssetId</c>) is
    /// included — staff reads get it, portal reads never do (spec §4: "No equipment, capacity or
    /// maintenance details").
    /// </summary>
    public static async Task<IReadOnlyList<CatalogOrderResponse>> EnrichAsync(
        AppDbContext db,
        IReadOnlyList<CatalogOrder> orders,
        bool includeAllocationDetail,
        CancellationToken cancellationToken)
    {
        if (orders.Count == 0)
        {
            return [];
        }

        var itemIds = orders.SelectMany(o => o.Items).Select(i => i.Id).ToList();

        var requirements = await db.CatalogOrderItemResourceRequirements
            .AsNoTracking()
            .Where(r => itemIds.Contains(r.OrderItemId))
            .ToListAsync(cancellationToken);

        var requirementIds = requirements.Select(r => r.Id).ToList();

        // Only the current (non-superseded) allocation matters for the production summary; Phase 0
        // never supersedes (no reschedule), so IsCurrent is always true here, but the filter keeps
        // this correct once Phase 1 adds reschedule.
        var allocations = requirementIds.Count == 0
            ? []
            : await db.CatalogOrderItemResourceAllocations
                .AsNoTracking()
                .Where(a => requirementIds.Contains(a.RequirementId) && a.IsCurrent)
                .ToListAsync(cancellationToken);

        var requirementsByItem = requirements.ToLookup(r => r.OrderItemId);
        var allocationByRequirement = allocations.ToDictionary(a => a.RequirementId);

        return orders.Select(order => Enrich(
            CatalogOrderService.MapOrder(order),
            order,
            requirementsByItem,
            allocationByRequirement,
            includeAllocationDetail)).ToList();
    }

    public static async Task<CatalogOrderResponse> EnrichOneAsync(
        AppDbContext db,
        CatalogOrder order,
        bool includeAllocationDetail,
        CancellationToken cancellationToken) =>
        (await EnrichAsync(db, [order], includeAllocationDetail, cancellationToken))[0];

    private static CatalogOrderResponse Enrich(
        CatalogOrderResponse baseResponse,
        CatalogOrder order,
        ILookup<Guid, CatalogOrderItemResourceRequirement> requirementsByItem,
        IReadOnlyDictionary<Guid, CatalogOrderItemResourceAllocation> allocationByRequirement,
        bool includeAllocationDetail)
    {
        DateTimeOffset? latestWorkEnd = null;
        var itemResponses = new List<CatalogOrderItemResponse>(baseResponse.Items.Count);
        var orderIsCancelledOrRejected = order.Status is CatalogOrderStatus.Cancelled or CatalogOrderStatus.Rejected;

        foreach (var item in baseResponse.Items)
        {
            var requirement = requirementsByItem[item.Id].FirstOrDefault();
            if (requirement is null)
            {
                itemResponses.Add(item with { ProductionSummary = ProductionSummaryStatus.NotRequired });
                continue;
            }

            var allocation = allocationByRequirement.GetValueOrDefault(requirement.Id);
            var summary = ComputeSummary(allocation, orderIsCancelledOrRejected);

            if (allocation is not null && allocation.Status is AllocationStatus.Scheduled or AllocationStatus.Running)
            {
                if (latestWorkEnd is null || allocation.PlannedWorkEnd > latestWorkEnd)
                {
                    latestWorkEnd = allocation.PlannedWorkEnd;
                }
            }

            var allocationDto = includeAllocationDetail && allocation is not null
                ? new CatalogOrderItemAllocationResponse(
                    allocation.Id,
                    allocation.RequirementId,
                    allocation.RentalAssetId,
                    allocation.Status,
                    allocation.PlannedWorkStart,
                    allocation.PlannedWorkEnd,
                    allocation.TimeZoneId,
                    allocation.ActualStartAt,
                    allocation.ActualWorkFinishedAt,
                    allocation.ActualReleaseAt,
                    allocation.PlanningRevision)
                : null;

            itemResponses.Add(item with { ProductionSummary = summary, Allocation = allocationDto });
        }

        // Order-level summary: worst-case across items (Phase 0 has at most one schedulable item, so
        // this is effectively that item's summary; the aggregation rule is written generically for
        // when Phase 1 allows multiple schedulable lines per order).
        var orderSummary = AggregateOrderSummary(itemResponses.Select(i => i.ProductionSummary));

        // estimatedReadyAt = latest required item's planned work end, excluding cleanup, once every
        // required item has reached at least Scheduled; null while unscheduled/no reliable estimate
        // (order-equipment-link-codex §5). Phase 0 has at most one schedulable item.
        var estimatedReadyAt = orderSummary is ProductionSummaryStatus.Scheduled or ProductionSummaryStatus.InProgress
            ? latestWorkEnd
            : orderSummary == ProductionSummaryStatus.Done
                ? latestWorkEnd
                : null;

        return baseResponse with
        {
            Items = itemResponses,
            OrderVersion = Convert.ToBase64String(order.RowVersion),
            PlanningRevision = order.PlanningRevision,
            ProductionSummary = orderSummary,
            EstimatedReadyAt = estimatedReadyAt,
        };
    }

    private static ProductionSummaryStatus ComputeSummary(
        CatalogOrderItemResourceAllocation? allocation,
        bool orderIsCancelledOrRejected)
    {
        if (allocation is null)
        {
            // A requirement snapshot exists but approval has not (yet, or ever) created an
            // allocation. Phase 0 has no persisted "Blocked" trigger (approval is atomic-or-rejected
            // — see approve flow), so Blocked never occurs from this path.
            return orderIsCancelledOrRejected
                ? ProductionSummaryStatus.Cancelled
                : ProductionSummaryStatus.Unscheduled;
        }

        return allocation.Status switch
        {
            AllocationStatus.Scheduled => ProductionSummaryStatus.Scheduled,
            AllocationStatus.Running => ProductionSummaryStatus.InProgress,
            AllocationStatus.Cleaning => ProductionSummaryStatus.Done,
            AllocationStatus.Released => ProductionSummaryStatus.Done,
            AllocationStatus.Cancelled => ProductionSummaryStatus.Cancelled,
            _ => ProductionSummaryStatus.Unscheduled,
        };
    }

    private static ProductionSummaryStatus AggregateOrderSummary(IEnumerable<ProductionSummaryStatus> items)
    {
        var list = items.ToList();
        if (list.Count == 0 || list.All(s => s == ProductionSummaryStatus.NotRequired))
        {
            return ProductionSummaryStatus.NotRequired;
        }

        var schedulable = list.Where(s => s != ProductionSummaryStatus.NotRequired).ToList();
        if (schedulable.Any(s => s == ProductionSummaryStatus.Blocked))
        {
            return ProductionSummaryStatus.Blocked;
        }

        if (schedulable.Any(s => s == ProductionSummaryStatus.Unscheduled))
        {
            return ProductionSummaryStatus.Unscheduled;
        }

        if (schedulable.All(s => s == ProductionSummaryStatus.Cancelled))
        {
            return ProductionSummaryStatus.Cancelled;
        }

        if (schedulable.Any(s => s == ProductionSummaryStatus.InProgress))
        {
            return ProductionSummaryStatus.InProgress;
        }

        if (schedulable.All(s => s is ProductionSummaryStatus.Done or ProductionSummaryStatus.Cancelled))
        {
            return ProductionSummaryStatus.Done;
        }

        return ProductionSummaryStatus.Scheduled;
    }
}
