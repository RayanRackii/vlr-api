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

public sealed class PlanningVersionConflictException(string message) : InvalidOperationException(message)
{
    public const string Code = "planning_version_conflict";
}

public sealed class UnsupportedWorkWindowException(string message) : ArgumentException(message)
{
    public const string Code = WorkWindowValidation.UnsupportedWorkWindowCode;
}

public interface ICatalogOrderAllocationService
{
    Task<CatalogOrderResponse> ApproveWithPlanningAsync(
        Guid orderId,
        CatalogOrderApprovePlanningRequest? planningRequest,
        CancellationToken cancellationToken);
}

/// <summary>
/// Atomic order approval with resource allocation (order-equipment-link-codex §3, "For every
/// approval"). Resource-free approvals (no schedulable requirement snapshot on any order item)
/// behave exactly as the pre-Phase-0 contract: no planning body required, no lock escalation
/// beyond what <see cref="CatalogOrderService"/>'s own transition already does.
/// </summary>
public sealed class CatalogOrderAllocationService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IResourceOccupancyService occupancy,
    IDomainEventDispatcher<CatalogOrderEvent> domainEvents,
    INotificationOutboxScheduler outboxScheduler) : ICatalogOrderAllocationService
{
    private static readonly string[] RequiredModules =
    [
        PlatformModules.Catalog,
        PlatformModules.Rentals,
        PlatformModules.ResourcePlanning,
    ];

    public async Task<CatalogOrderResponse> ApproveWithPlanningAsync(
        Guid orderId,
        CatalogOrderApprovePlanningRequest? planningRequest,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();

        var order = await dbContext.CatalogOrders
            .Include(o => o.Items)
            .Include(o => o.History)
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken)
            ?? throw new KeyNotFoundException("Order not found.");

        var itemIds = order.Items.Select(i => i.Id).ToList();
        var requirements = itemIds.Count == 0
            ? []
            : await dbContext.CatalogOrderItemResourceRequirements
                .Where(r => itemIds.Contains(r.OrderItemId))
                .ToListAsync(cancellationToken);

        // Phase 0 scope: "one scheduled line per order". More than one snapshot would be a
        // data/logic bug from a later phase, not a crash here — only the first is planned.
        var requirement = requirements.OrderBy(r => r.Id).FirstOrDefault();

        if (requirement is null)
        {
            // Ordinary (non-scheduled) order: resource-free approvals retain their current
            // contract exactly, including no planning-lock escalation.
            return await ApproveWithoutPlanningAsync(order, cancellationToken);
        }

        if (planningRequest is null || planningRequest.UnitId is null || planningRequest.WorkStart is null)
        {
            throw new ArgumentException(
                "unitId and workStart are required to approve an order with a scheduled resource requirement.");
        }

        await domainEvents.EnsureReadyAsync(cancellationToken);

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            // Step 1: acquire the tenant module lock and recheck entitlements (same advisory-lock
            // convention as AdminTenantService.LockTenantModulesAsync), so module disablement and
            // allocation admission serialize against each other.
            await LockTenantModulesAsync(tenantId, cancellationToken);
            await EnsureRequiredModulesActiveAsync(tenantId, cancellationToken);

            // Step 2: lock the order row, then candidate resources ascending. The order lock has
            // already effectively been taken by loading `order` as tracked above inside this
            // transaction (Postgres row lock is acquired at UPDATE time; re-reading isn't needed
            // because no other writer can commit a conflicting RowVersion change without blocking
            // on this same transaction once we SaveChanges). Resource locks come next, never the
            // reverse.
            if (requirement.RentalAssetId is not { } rentalAssetId)
            {
                throw new ArgumentException("Category-based resource selection is not supported yet.");
            }

            await occupancy.LockResourcesAsync(dbContext, tenantId, [rentalAssetId], cancellationToken);

            // Idempotent retry: if the order is already Approved and an allocation already exists
            // for this requirement, return the existing result rather than erroring or duplicating.
            var existingAllocation = await dbContext.CatalogOrderItemResourceAllocations
                .FirstOrDefaultAsync(
                    a => a.RequirementId == requirement.Id && a.IsCurrent && a.Status != AllocationStatus.Cancelled,
                    cancellationToken);

            if (order.Status != CatalogOrderStatus.Requested)
            {
                if (existingAllocation is not null)
                {
                    if (transaction is not null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                    }

                    return await CatalogOrderPlanningMapper.EnrichOneAsync(dbContext, order, includeAllocationDetail: true, cancellationToken);
                }

                throw new InvalidCatalogOrderTransitionException($"Cannot transition order from {order.Status}.");
            }

            if (existingAllocation is not null)
            {
                // Should not normally happen (order still Requested but an allocation already
                // exists) — treat as a lost-response retry anyway rather than duplicating.
                order.Approve(CatalogActorType.System, actorId: null);
                await dbContext.SaveChangesAsync(cancellationToken);
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return await CatalogOrderPlanningMapper.EnrichOneAsync(dbContext, order, includeAllocationDetail: true, cancellationToken);
            }

            // Step 3: reload calendar/eligibility/occupancy under the locks just taken.
            if (planningRequest.ExpectedVersion is { } expectedVersion && expectedVersion != order.PlanningRevision)
            {
                throw new PlanningVersionConflictException(
                    "The order's planning state changed since this approval was prepared; reload and retry.");
            }

            var eligibleResource = await occupancy.ResolveEligibleResourceAsync(
                dbContext, tenantId, rentalAssetId, cancellationToken)
                ?? throw new KeyNotFoundException("The configured resource was not found for this tenant.");

            if (!eligibleResource.IsEligible)
            {
                throw new ResourceUnavailableException(
                    rentalAssetId,
                    eligibleResource.IneligibilityReason
                        ?? "The configured resource is not currently available for production use.");
            }

            // Step 4: validate the work window and compute the buffered interval.
            var orderItem = order.Items.First(i => i.Id == requirement.OrderItemId);
            var workStart = planningRequest.WorkStart.Value;
            var workEnd = workStart + TimeSpan.FromMinutes((double)requirement.DurationMinutesPerUnit * orderItem.Quantity);
            var occupiedStart = workStart - TimeSpan.FromMinutes(requirement.PrepMinutes);
            var occupiedEnd = workEnd + TimeSpan.FromMinutes(requirement.CleanupMinutes);

            var windowValidation = await occupancy.ValidateWorkWindowAsync(
                dbContext, tenantId, rentalAssetId, occupiedStart, occupiedEnd, cancellationToken);
            if (windowValidation.Error is { } windowError)
            {
                throw new UnsupportedWorkWindowException(windowError);
            }

            var zoneId = windowValidation.ZoneId;

            // Step 5: check availability, then create the InternalUse occupancy. On
            // ResourceUnavailableException the whole transaction rolls back below and the order
            // stays Requested.
            var availability = await occupancy.CheckAvailabilityAsync(
                dbContext, tenantId, rentalAssetId, occupiedStart, occupiedEnd, tentativeBatch: [], cancellationToken);
            if (!availability.IsAvailable)
            {
                throw new ResourceUnavailableException(
                    rentalAssetId,
                    availability.Reason ?? $"Resource '{rentalAssetId}' is not available for the requested interval.");
            }

            var reservationItemId = await occupancy.CreateInternalUseOccupancyAsync(
                dbContext,
                tenantId,
                planningRequest.UnitId.Value,
                rentalAssetId,
                requirement.ResourceQuantity,
                occupiedStart,
                occupiedEnd,
                cancellationToken);

            // Step 6: create the allocation row, approve the order, bump PlanningRevision.
            order.PlanningRevision += 1;
            var allocation = new CatalogOrderItemResourceAllocation
            {
                TenantId = tenantId,
                RequirementId = requirement.Id,
                ReservationItemId = reservationItemId,
                Sequence = 1,
                PlanningRevision = order.PlanningRevision,
                IsCurrent = true,
                AllocatedQuantity = requirement.ResourceQuantity,
                RentalAssetId = rentalAssetId,
                PlannedWorkStart = workStart,
                PlannedWorkEnd = workEnd,
                TimeZoneId = zoneId,
                Status = AllocationStatus.Scheduled,
            };
            dbContext.CatalogOrderItemResourceAllocations.Add(allocation);

            order.Approve(CatalogActorType.System, actorId: null);

            var queued = await domainEvents.DispatchAsync(
                new CatalogOrderEvent(order, CatalogEventTypes.OrderApproved),
                cancellationToken);

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new InvalidCatalogOrderTransitionException("The order was modified by another user.");
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            foreach (var deliveryId in queued)
            {
                outboxScheduler.Schedule(deliveryId);
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

    private async Task<CatalogOrderResponse> ApproveWithoutPlanningAsync(
        CatalogOrder order,
        CancellationToken cancellationToken)
    {
        await domainEvents.EnsureReadyAsync(cancellationToken);

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            order.Approve(CatalogActorType.System, actorId: null);

            var queued = await domainEvents.DispatchAsync(
                new CatalogOrderEvent(order, CatalogEventTypes.OrderApproved),
                cancellationToken);

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new InvalidCatalogOrderTransitionException("The order was modified by another user.");
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            foreach (var deliveryId in queued)
            {
                outboxScheduler.Schedule(deliveryId);
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

    private async Task LockTenantModulesAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (dbContext.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) != true)
        {
            return;
        }

        var key = $"tenant-modules:{tenantId}";
        const long seed = 0;
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, {seed}))",
            cancellationToken);
    }

    private async Task EnsureRequiredModulesActiveAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var active = await dbContext.TenantModules
            .Where(m => m.TenantId == tenantId && m.IsActive && RequiredModules.Contains(m.ModuleName))
            .Select(m => m.ModuleName)
            .ToListAsync(cancellationToken);

        var missing = RequiredModules.Except(active).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Cannot allocate a resource: module(s) {string.Join(", ", missing)} are not active for this tenant.");
        }
    }

    private Guid EnsureTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");
}
