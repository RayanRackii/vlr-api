using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Rentals.Contracts;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Domain.Exceptions;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Catalog.Services;

public interface ICatalogOrderAllocationUsageService
{
    Task<CatalogOrderResponse> ApplyUsageActionAsync(
        Guid orderId,
        Guid allocationId,
        CatalogOrderAllocationUsageRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Standalone allocation-usage transitions (start/work-finished/release), exposed for explicit or
/// out-of-band usage changes — e.g. production actually starting later than the commercial
/// "preparing" status, or releasing a resource well after the order reached Ready/Completed.
/// <see cref="CatalogAllocationExecutionService"/> drives the common case automatically from the
/// existing preparing/ready actions; this endpoint covers everything else, including release
/// (which always happens here — see order-equipment-link-codex §3/§7).
/// </summary>
public sealed class CatalogOrderAllocationUsageService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IResourceOccupancyService occupancy) : ICatalogOrderAllocationUsageService
{
    public async Task<CatalogOrderResponse> ApplyUsageActionAsync(
        Guid orderId,
        Guid allocationId,
        CatalogOrderAllocationUsageRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();

        var order = await dbContext.CatalogOrders
            .Include(o => o.Items)
            .Include(o => o.History)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.TenantId == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Order not found.");

        var itemIds = order.Items.Select(i => i.Id).ToList();
        var requirementIds = await dbContext.CatalogOrderItemResourceRequirements
            .Where(r => itemIds.Contains(r.OrderItemId))
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

        var allocation = await dbContext.CatalogOrderItemResourceAllocations
            .FirstOrDefaultAsync(
                a => a.Id == allocationId && a.TenantId == tenantId && requirementIds.Contains(a.RequirementId),
                cancellationToken)
            ?? throw new KeyNotFoundException("Allocation not found for this order.");

        if (allocation.PlanningRevision != request.ExpectedVersion)
        {
            throw new PlanningVersionConflictException(
                "The allocation changed since this action was prepared; reload and retry.");
        }

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            switch (request.Action.Trim().ToLowerInvariant())
            {
                case "start":
                    Start(order, allocation);
                    break;
                case "work-finished":
                    WorkFinished(allocation);
                    break;
                case "release":
                    await ReleaseAsync(order, allocation, cancellationToken);
                    break;
                default:
                    throw new ArgumentException($"Unknown usage action '{request.Action}'.");
            }

            order.PlanningRevision += 1;
            allocation.PlanningRevision = order.PlanningRevision;
            allocation.Touch();

            await dbContext.SaveChangesAsync(cancellationToken);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return await CatalogOrderPlanningMapper.EnrichOneAsync(dbContext, order, includeAllocationDetail: true, cancellationToken);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
    }

    private static void Start(CatalogOrder order, CatalogOrderItemResourceAllocation allocation)
    {
        // Starting a cancelled or rejected order is forbidden, even though Running/Cleaning
        // allocations from before cancellation may still be actively executing — those are
        // advanced via work-finished/release, never (re)started.
        if (order.Status is CatalogOrderStatus.Cancelled or CatalogOrderStatus.Rejected)
        {
            throw new InvalidCatalogOrderTransitionException(
                $"Cannot start production: order is {order.Status}.");
        }

        if (allocation.Status != AllocationStatus.Scheduled)
        {
            throw new InvalidCatalogOrderTransitionException(
                $"Cannot start: allocation is {allocation.Status}, expected Scheduled.");
        }

        allocation.Status = AllocationStatus.Running;
        allocation.ActualStartAt = DateTimeOffset.UtcNow;
    }

    private static void WorkFinished(CatalogOrderItemResourceAllocation allocation)
    {
        if (allocation.Status != AllocationStatus.Running)
        {
            throw new InvalidCatalogOrderTransitionException(
                $"Cannot mark work finished: allocation is {allocation.Status}, expected Running.");
        }

        allocation.Status = AllocationStatus.Cleaning;
        allocation.ActualWorkFinishedAt = DateTimeOffset.UtcNow;
    }

    private async Task ReleaseAsync(
        CatalogOrder order,
        CatalogOrderItemResourceAllocation allocation,
        CancellationToken cancellationToken)
    {
        // Release from Scheduled (never started) is also valid — it is modeled as Cancelled
        // (released without ever running), distinct from Released (ran, cleaned up, then
        // released), matching the two cancellation-origin cases the lifecycle enum distinguishes.
        if (allocation.Status == AllocationStatus.Scheduled)
        {
            await occupancy.ReleaseInternalUseOccupancyAsync(
                dbContext, order.TenantId, allocation.ReservationItemId, cancellationToken);
            allocation.Status = AllocationStatus.Cancelled;
            allocation.ActualReleaseAt = DateTimeOffset.UtcNow;
            return;
        }

        if (allocation.Status != AllocationStatus.Cleaning)
        {
            throw new InvalidCatalogOrderTransitionException(
                $"Cannot release: allocation is {allocation.Status}, expected Cleaning or Scheduled.");
        }

        await occupancy.ReleaseInternalUseOccupancyAsync(
            dbContext, order.TenantId, allocation.ReservationItemId, cancellationToken);
        allocation.Status = AllocationStatus.Released;
        allocation.ActualReleaseAt = DateTimeOffset.UtcNow;
    }

    private Guid EnsureTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");
}
