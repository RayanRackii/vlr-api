using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Contracts;
using Platform.Api.Modules.ResourcePlanning.Contracts;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Domain.Exceptions;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Catalog.Services;

/// <summary>
/// Planning hooks invoked by <see cref="CatalogOrderService"/>'s existing commercial transitions
/// (preparing/ready/reject/cancel), per order-equipment-link-codex §5: "Existing preparing and
/// ready actions invoke the internal execution operations." Each hook operates on the order's
/// current (IsCurrent) allocation(s), inside the same transaction/DbContext as the commercial
/// mutation, so a planning failure rolls back the commercial change too. The occupancy is held by
/// the allocation's planning record; every status change is mirrored onto it and freed through the
/// planning port.
/// </summary>
internal static class CatalogAllocationExecutionService
{
    /// <summary>Preparing: any pending (Scheduled) allocation starts running.</summary>
    public static async Task StartPendingAllocationHook(
        IAssetOccupancyService assetOccupancy,
        AppDbContext db,
        CatalogOrder order,
        CancellationToken cancellationToken)
    {
        var (allocations, planning) = await LoadCurrentAllocationsAsync(assetOccupancy, db, order, cancellationToken);
        foreach (var allocation in allocations.Where(a => a.Status == AllocationStatus.Scheduled))
        {
            allocation.Status = AllocationStatus.Running;
            allocation.ActualStartAt = DateTimeOffset.UtcNow;
            allocation.RowVersion = Guid.NewGuid().ToByteArray();
            allocation.Touch();
            CatalogAllocationMirror.Apply(allocation, PlanningOf(planning, allocation));
        }
    }

    /// <summary>Ready requires every required item's allocation to have at least finished work.</summary>
    public static async Task RequireWorkFinishedHook(
        IAssetOccupancyService assetOccupancy,
        AppDbContext db,
        CatalogOrder order,
        CancellationToken cancellationToken)
    {
        var (allocations, _) = await LoadCurrentAllocationsAsync(assetOccupancy, db, order, cancellationToken);
        var notFinished = allocations
            .Where(a => a.Status is AllocationStatus.Scheduled or AllocationStatus.Running)
            .ToList();

        if (notFinished.Count > 0)
        {
            throw new InvalidCatalogOrderTransitionException(
                "Cannot mark the order ready: production work has not finished for every required resource.");
        }
    }

    /// <summary>
    /// Cancel/reject: unstarted (Scheduled) allocations are released transactionally; an
    /// already-started (Running/Cleaning) allocation keeps the resource occupied until explicit
    /// release via the usage endpoint — the order becoming terminal does not touch it.
    /// </summary>
    public static async Task ReleaseUnstartedAllocationsHook(
        IAssetOccupancyService assetOccupancy,
        IResourceOccupancyService occupancy,
        AppDbContext db,
        CatalogOrder order,
        CancellationToken cancellationToken)
    {
        var (allocations, planning) = await LoadCurrentAllocationsAsync(assetOccupancy, db, order, cancellationToken);

        foreach (var allocation in allocations.Where(a => a.Status == AllocationStatus.Scheduled))
        {
            allocation.Status = AllocationStatus.Cancelled;
            allocation.ActualReleaseAt = DateTimeOffset.UtcNow;
            allocation.RowVersion = Guid.NewGuid().ToByteArray();
            allocation.Touch();

            var record = PlanningOf(planning, allocation);
            CatalogAllocationMirror.Apply(allocation, record);
            await CatalogAllocationMirror.ReleaseOccupancyAsync(
                assetOccupancy, occupancy, db, order.TenantId, allocation, record,
                InternalUseReleaseKind.CancelBeforeStart, cancellationToken);
        }
    }

    private static AssetAllocation? PlanningOf(
        IReadOnlyDictionary<Guid, AssetAllocation> planning,
        CatalogOrderItemResourceAllocation allocation) =>
        allocation.AssetAllocationId is { } id && planning.TryGetValue(id, out var record) ? record : null;

    private static async Task<(List<CatalogOrderItemResourceAllocation> Allocations, Dictionary<Guid, AssetAllocation> Planning)>
        LoadCurrentAllocationsAsync(
            IAssetOccupancyService assetOccupancy,
            AppDbContext db,
            CatalogOrder order,
            CancellationToken cancellationToken)
    {
        var itemIds = order.Items.Select(i => i.Id).ToList();
        if (itemIds.Count == 0)
        {
            return ([], new Dictionary<Guid, AssetAllocation>());
        }

        var requirementIds = await db.CatalogOrderItemResourceRequirements
            .Where(r => itemIds.Contains(r.OrderItemId))
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

        if (requirementIds.Count == 0)
        {
            return ([], new Dictionary<Guid, AssetAllocation>());
        }

        // Discover assets without trusting tracked allocation state, then reload under the same
        // asset locks used by approval and standalone usage actions.
        var links = await db.CatalogOrderItemResourceAllocations.AsNoTracking()
            .Where(a => requirementIds.Contains(a.RequirementId) && a.IsCurrent)
            .Select(a => new { a.AssetAllocationId, a.RentalAssetId })
            .ToListAsync(cancellationToken);
        var assetIds = await CatalogOrderPlanner.ResolveAllocationAssetIdsAsync(
            db,
            order.TenantId,
            links.Select(l => (l.AssetAllocationId, l.RentalAssetId)).ToList(),
            cancellationToken);
        await assetOccupancy.LockAssetsAsync(order.TenantId, assetIds, cancellationToken);
        var allocations = await db.CatalogOrderItemResourceAllocations
            .Where(a => requirementIds.Contains(a.RequirementId) && a.IsCurrent)
            .ToListAsync(cancellationToken);
        foreach (var allocation in allocations)
        {
            await db.Entry(allocation).ReloadAsync(cancellationToken);
        }

        var planning = await CatalogAllocationMirror.LoadPlanningAsync(db, order.TenantId, allocations, cancellationToken);
        return (allocations, planning);
    }
}
