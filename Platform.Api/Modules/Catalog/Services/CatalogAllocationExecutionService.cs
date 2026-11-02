using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Contracts;
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
/// mutation, so a planning failure rolls back the commercial change too.
/// </summary>
internal static class CatalogAllocationExecutionService
{
    /// <summary>Preparing: any pending (Scheduled) allocation starts running.</summary>
    public static async Task StartPendingAllocationHook(
        AppDbContext db,
        CatalogOrder order,
        CancellationToken cancellationToken)
    {
        var allocations = await LoadCurrentAllocationsAsync(db, order, cancellationToken);
        foreach (var allocation in allocations.Where(a => a.Status == AllocationStatus.Scheduled))
        {
            allocation.Status = AllocationStatus.Running;
            allocation.ActualStartAt = DateTimeOffset.UtcNow;
            allocation.Touch();
        }
    }

    /// <summary>Ready requires every required item's allocation to have at least finished work.</summary>
    public static async Task RequireWorkFinishedHook(
        AppDbContext db,
        CatalogOrder order,
        CancellationToken cancellationToken)
    {
        var allocations = await LoadCurrentAllocationsAsync(db, order, cancellationToken);
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
        IResourceOccupancyService occupancy,
        AppDbContext db,
        CatalogOrder order,
        CancellationToken cancellationToken)
    {
        var allocations = await LoadCurrentAllocationsAsync(db, order, cancellationToken);

        foreach (var allocation in allocations.Where(a => a.Status == AllocationStatus.Scheduled))
        {
            await occupancy.ReleaseInternalUseOccupancyAsync(
                db, order.TenantId, allocation.ReservationItemId, cancellationToken);
            allocation.Status = AllocationStatus.Cancelled;
            allocation.ActualReleaseAt = DateTimeOffset.UtcNow;
            allocation.Touch();
        }
    }

    private static async Task<List<CatalogOrderItemResourceAllocation>> LoadCurrentAllocationsAsync(
        AppDbContext db,
        CatalogOrder order,
        CancellationToken cancellationToken)
    {
        var itemIds = order.Items.Select(i => i.Id).ToList();
        if (itemIds.Count == 0)
        {
            return [];
        }

        var requirementIds = await db.CatalogOrderItemResourceRequirements
            .Where(r => itemIds.Contains(r.OrderItemId))
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

        if (requirementIds.Count == 0)
        {
            return [];
        }

        return await db.CatalogOrderItemResourceAllocations
            .Where(a => requirementIds.Contains(a.RequirementId) && a.IsCurrent)
            .ToListAsync(cancellationToken);
    }
}
