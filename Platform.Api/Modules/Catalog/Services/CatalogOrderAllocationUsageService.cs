using Microsoft.EntityFrameworkCore;
using Platform.Api.DomainEvents;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Rentals.Contracts;
using Platform.Api.Notifications;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Domain.Events;
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
/// Stable <see cref="CatalogOrderStatusHistory.Reason"/> codes for standalone usage actions. The
/// history schema has no dedicated action column, so the reason is the carrier that distinguishes
/// a usage transition (work finished / resource released) from a commercial status change.
/// </summary>
public static class CatalogOrderUsageHistoryReasons
{
    public const string WorkFinished = "usage.workFinished";
    public const string Released = "usage.released";
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
    IResourceOccupancyService occupancy,
    IHttpContextAccessor? httpContextAccessor = null,
    IDomainEventDispatcher<CatalogOrderEvent>? domainEvents = null,
    INotificationOutboxScheduler? outboxScheduler = null) : ICatalogOrderAllocationUsageService
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

        var (actorType, actorId) = await CatalogOrderActorResolver.ResolveAsync(
            httpContextAccessor, dbContext, cancellationToken);

        var itemIds = order.Items.Select(i => i.Id).ToList();
        var requirementIds = await dbContext.CatalogOrderItemResourceRequirements
            .Where(r => itemIds.Contains(r.OrderItemId))
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

        // Seed notification templates/channel defaults before the transaction (as the commercial
        // transitions do) so an auto-Ready publish inside the transaction finds the OrderReady
        // channel config already present.
        if (domainEvents is not null)
        {
            await domainEvents.EnsureReadyAsync(cancellationToken);
        }

        // Pre-lock lookup only to find which resource to lock; never trusted for the actual
        // transition — the allocation is reloaded fresh once the lock is held, below.
        var rentalAssetId = await dbContext.CatalogOrderItemResourceAllocations
            .AsNoTracking()
            .Where(a => a.Id == allocationId && a.TenantId == tenantId && requirementIds.Contains(a.RequirementId))
            .Select(a => (Guid?)a.RentalAssetId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("Allocation not found for this order.");

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            // H3: serialize concurrent usage actions on the same allocation (e.g. cancel vs start)
            // through the same resource-row lock the approval flow already uses, rather than
            // relying on the PlanningRevision check alone — then reload the allocation under that
            // lock so the check below sees the latest committed state, not a stale pre-lock read.
            await occupancy.LockResourcesAsync(dbContext, tenantId, [rentalAssetId], cancellationToken);

            var allocation = await dbContext.CatalogOrderItemResourceAllocations
                .FirstOrDefaultAsync(
                    a => a.Id == allocationId && a.TenantId == tenantId && requirementIds.Contains(a.RequirementId),
                    cancellationToken)
                ?? throw new KeyNotFoundException("Allocation not found for this order.");

            await dbContext.Entry(allocation).ReloadAsync(cancellationToken);

            if (allocation.PlanningRevision != request.ExpectedVersion)
            {
                throw new PlanningVersionConflictException(
                    "The allocation changed since this action was prepared; reload and retry.");
            }

            IReadOnlyList<Guid> queued = [];
            switch (request.Action.Trim().ToLowerInvariant())
            {
                case "start":
                    Start(order, allocation);
                    break;
                case "work-finished":
                    WorkFinished(allocation);
                    // Record the usage action against the current (still Preparing) status before
                    // any auto-Ready advances it, so the history reads work-finished → ready.
                    order.RecordHistory(order.Status, actorType, actorId, CatalogOrderUsageHistoryReasons.WorkFinished);
                    queued = await TryMarkReadyAsync(order, requirementIds, actorType, actorId, cancellationToken);
                    break;
                case "release":
                    await ReleaseAsync(order, allocation, cancellationToken);
                    order.RecordHistory(order.Status, actorType, actorId, CatalogOrderUsageHistoryReasons.Released);
                    break;
                default:
                    throw new ArgumentException($"Unknown usage action '{request.Action}'.");
            }

            order.PlanningRevision += 1;
            order.RowVersion = Guid.NewGuid().ToByteArray();
            allocation.PlanningRevision = order.PlanningRevision;
            allocation.RowVersion = Guid.NewGuid().ToByteArray();
            allocation.Touch();

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new PlanningVersionConflictException(
                    "The allocation changed since this action was prepared; reload and retry.");
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            if (outboxScheduler is not null)
            {
                foreach (var deliveryId in queued)
                {
                    outboxScheduler.Schedule(deliveryId);
                }
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

    /// <summary>
    /// After work finishes, auto-advance a Preparing order to Ready once every current resource
    /// allocation has finished work (Cleaning/Released/Cancelled) — the same rule
    /// <see cref="CatalogAllocationExecutionService.RequireWorkFinishedHook"/> enforces for the
    /// explicit ready action — and publish the OrderReady notification the same way
    /// <c>CatalogOrderService.MarkReadyAsync</c> does. Orders without resources and partial
    /// completion (an allocation still Scheduled/Running) are left untouched.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> TryMarkReadyAsync(
        CatalogOrder order,
        List<Guid> requirementIds,
        CatalogActorType actorType,
        Guid? actorId,
        CancellationToken cancellationToken)
    {
        if (order.Status != CatalogOrderStatus.Preparing || requirementIds.Count == 0)
        {
            return [];
        }

        var allocations = await dbContext.CatalogOrderItemResourceAllocations
            .Where(a => requirementIds.Contains(a.RequirementId) && a.IsCurrent)
            .ToListAsync(cancellationToken);

        var workFinished = allocations.Count > 0
            && allocations.All(a => a.Status is AllocationStatus.Cleaning
                or AllocationStatus.Released
                or AllocationStatus.Cancelled);

        if (!workFinished)
        {
            return [];
        }

        order.MarkReady(actorType, actorId);

        if (domainEvents is null)
        {
            return [];
        }

        return await domainEvents.DispatchAsync(
            new CatalogOrderEvent(order, CatalogEventTypes.OrderReady),
            cancellationToken);
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
                dbContext, order.TenantId, allocation.ReservationItemId,
                InternalUseReleaseKind.CancelBeforeStart, allocation.PlannedWorkEnd, cancellationToken);
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
            dbContext, order.TenantId, allocation.ReservationItemId,
            InternalUseReleaseKind.ReleaseAfterStart, allocation.PlannedWorkEnd, cancellationToken);
        allocation.Status = AllocationStatus.Released;
        allocation.ActualReleaseAt = DateTimeOffset.UtcNow;
    }

    private Guid EnsureTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");
}
