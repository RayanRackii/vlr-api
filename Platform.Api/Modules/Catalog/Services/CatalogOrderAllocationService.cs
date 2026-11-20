using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Platform.Api.DomainEvents;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Rentals.Contracts;
using Platform.Api.Modules.ResourcePlanning.Contracts;
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

/// <summary>
/// H5 Phase 0 decision: only a single schedulable requirement per product/order is supported.
/// Batch allocation across more than one line is out of scope — raised both when a product
/// config would activate more than one requirement (400) and when approving an order whose items
/// carry more than one requirement snapshot needing planning (409); each controller maps the
/// status code for its own endpoint.
/// </summary>
public sealed class UnsupportedRequirementScopeException(string message) : InvalidOperationException(message)
{
    public const string Code = "unsupported_requirement_scope";
}

public interface ICatalogOrderAllocationService
{
    Task<CatalogOrderResponse> ApproveWithPlanningAsync(
        Guid orderId,
        CatalogOrderApprovePlanningRequest? planningRequest,
        CancellationToken cancellationToken);

    /// <summary>
    /// True when the order has at least one resource-requirement snapshot — i.e. approving or
    /// starting production on it affects planning, so the caller must additionally hold
    /// catalog.orders.allocate (H2: orders.manage alone must not be sufficient for a planning
    /// transition). Resource-free orders return false and keep their pre-Phase-0 permission set.
    /// </summary>
    Task<bool> HasSchedulableRequirementAsync(Guid orderId, CancellationToken cancellationToken);
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
    INotificationOutboxScheduler outboxScheduler,
    IHttpContextAccessor httpContextAccessor,
    IAssetOccupancyService? assetOccupancyService = null) : ICatalogOrderAllocationService
{
    // Rentals is not listed: it is required only when the target asset is rental-backed, and the
    // planning port (which knows whether a rental configuration is in force) decides that.
    private static readonly string[] RequiredModules =
    [
        PlatformModules.Catalog,
        PlatformModules.ResourcePlanning,
    ];

    private readonly IAssetOccupancyService assetOccupancy =
        AssetOccupancyFactory.Create(assetOccupancyService, dbContext, tenantProvider, occupancy);

    public async Task<bool> HasSchedulableRequirementAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        var itemIds = await dbContext.CatalogOrderItems
            .AsNoTracking()
            .Where(i => i.TenantId == tenantId && i.OrderId == orderId)
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);

        if (itemIds.Count == 0)
        {
            return false;
        }

        return await dbContext.CatalogOrderItemResourceRequirements
            .AsNoTracking()
            .AnyAsync(r => r.TenantId == tenantId && itemIds.Contains(r.OrderItemId), cancellationToken);
    }

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

        var (actorType, actorId) = await ResolveActorAsync(cancellationToken);

        var itemIds = order.Items.Select(i => i.Id).ToList();
        var requirements = itemIds.Count == 0
            ? []
            : await dbContext.CatalogOrderItemResourceRequirements
                .Where(r => itemIds.Contains(r.OrderItemId))
                .ToListAsync(cancellationToken);

        // H5 Phase 0 decision: "one scheduled line per order" — more than one requirement
        // snapshot is rejected outright rather than silently planning only the first one.
        if (requirements.Count > 1)
        {
            throw new UnsupportedRequirementScopeException(
                "This order has more than one resource-requirement line; batch allocation is not supported yet.");
        }

        var requirement = requirements.FirstOrDefault();

        if (requirement is null)
        {
            // Ordinary (non-scheduled) order: resource-free approvals retain their current
            // contract exactly, including no planning-lock escalation.
            return await ApproveWithoutPlanningAsync(order, actorType, actorId, cancellationToken);
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
            if (requirement.AssetId is null && requirement.RentalAssetId is null)
            {
                throw new ArgumentException("Category-based resource selection is not supported yet.");
            }

            // The planning port locks the asset row, then the rental-asset rows of a rental-backed
            // asset, in that order (lock order of the planning spec).
            var assetId = await CatalogOrderPlanner.ResolveAssetIdAsync(
                dbContext, tenantId, requirement.AssetId, requirement.RentalAssetId, cancellationToken);
            await assetOccupancy.LockAssetsAsync(tenantId, [assetId], cancellationToken);

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
                order.Approve(actorType, actorId);
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

            var asset = await CatalogOrderPlanner.ResolveAsync(
                assetOccupancy, occupancy, dbContext, tenantId, assetId, requirement.RentalAssetId, cancellationToken);

            if (asset.RentalAssetId is { } rentalAssetId)
            {
                // Rental-backed: the rentals eligibility rules, as in Phase 0.
                var eligibleResource = await occupancy.ResolveEligibleResourceAsync(
                    dbContext, tenantId, rentalAssetId, cancellationToken)
                    ?? throw new KeyNotFoundException(CatalogOrderPlanner.ResourceNotFound);

                if (!eligibleResource.IsEligible)
                {
                    throw new ResourceUnavailableException(
                        rentalAssetId,
                        eligibleResource.IneligibilityReason
                            ?? "The configured resource is not currently available for production use.");
                }
            }
            else
            {
                await CatalogOrderPlanner.EnsureUnitAsync(
                    dbContext, tenantId, planningRequest.UnitId.Value, asset, cancellationToken);

                if (requirement.ResourceQuantity > asset.Capacity)
                {
                    throw new ResourceUnavailableException(
                        asset.AssetId,
                        $"'{asset.Name}' cannot hold {requirement.ResourceQuantity} units at once.");
                }
            }

            // Step 4: validate the work window and compute the buffered interval.
            var orderItem = order.Items.First(i => i.Id == requirement.OrderItemId);
            // Normalize to UTC at the boundary: a workStart carrying a non-zero offset (e.g. -03:00)
            // is a valid instant, but PostgreSQL's timestamptz parameters require offset 0 — without
            // this the InternalUse reservation write would throw a raw driver error (surfaced as a
            // 500), rather than the request succeeding against the same instant.
            var workStart = planningRequest.WorkStart.Value.ToUniversalTime();
            var (workEnd, _, _) = CatalogPlanningWindow.Calculate(
                workStart, requirement.DurationMinutesPerUnit, orderItem.Quantity,
                requirement.PrepMinutes, requirement.CleanupMinutes, CatalogOrderPlanner.MaxWorkMinutes(asset));

            // The port applies the window rules of the asset's calendar authority: the offerable
            // booking window (single day) of a rental-backed asset, the working-hours policy of a
            // planning-only one.
            var window = await CatalogOrderPlanner.RunAsync(asset, () => assetOccupancy.ValidateWindowAsync(
                tenantId,
                asset,
                AssetAllocationKind.Use,
                workStart,
                workEnd,
                requirement.PrepMinutes,
                requirement.CleanupMinutes,
                cancellationToken));
            CatalogOrderPlanner.ThrowIfRejected(window);

            var zoneId = asset.TimeZoneId;

            // Step 5: check availability, then occupy the asset (the planning record and, on a
            // rental-backed asset, its InternalUse reservation). On ResourceUnavailableException the
            // whole transaction rolls back below and the order stays Requested.
            var availability = await CatalogOrderPlanner.RunAsync(asset, () => assetOccupancy.CheckAvailabilityAsync(
                tenantId, asset, AssetAllocationKind.Use, window, excludeAllocationId: null, cancellationToken));
            if (!availability.IsFree)
            {
                // Surface the resource's display name, never the raw id (bug 4).
                throw new ResourceUnavailableException(
                    asset.RentalAssetId ?? asset.AssetId,
                    $"'{asset.Name}' is already booked for an overlapping interval.");
            }

            var planning = await CatalogOrderPlanner.RunAsync(asset, () => assetOccupancy.OccupyAsync(
                tenantId,
                asset,
                new NewAllocation(
                    AssetAllocationKind.Use,
                    CatalogOrderPlanner.Title(order, orderItem),
                    Notes: null,
                    window,
                    actorId,
                    AssetAllocationSource.CatalogOrder,
                    requirement.OrderItemId,
                    CatalogOrderPlanner.ReferenceLabel(order),
                    planningRequest.UnitId,
                    requirement.ResourceQuantity),
                cancellationToken));

            // Step 6: create the allocation row, approve the order, bump PlanningRevision.
            order.PlanningRevision += 1;
            var allocation = new CatalogOrderItemResourceAllocation
            {
                TenantId = tenantId,
                RequirementId = requirement.Id,
                AssetAllocationId = planning.Id,
                ReservationItemId = planning.ReservationItemId,
                Sequence = 1,
                PlanningRevision = order.PlanningRevision,
                IsCurrent = true,
                AllocatedQuantity = requirement.ResourceQuantity,
                RentalAssetId = asset.RentalAssetId,
                PlannedWorkStart = workStart,
                PlannedWorkEnd = workEnd,
                TimeZoneId = zoneId,
                Status = AllocationStatus.Scheduled,
            };
            dbContext.CatalogOrderItemResourceAllocations.Add(allocation);

            order.Approve(actorType, actorId);

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
        CatalogActorType actorType,
        Guid? actorId,
        CancellationToken cancellationToken)
    {
        await domainEvents.EnsureReadyAsync(cancellationToken);

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            order.Approve(actorType, actorId);

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
            throw new RequiredModuleInactiveException(missing);
        }
    }

    private Guid EnsureTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");

    /// <summary>
    /// Resolves the acting party for an approval. A request carrying an authenticated staff
    /// principal maps to that <see cref="CatalogActorType.B2BUser"/> (so order history attributes
    /// the approval to the real user); only genuinely automatic paths with no principal stay
    /// <see cref="CatalogActorType.System"/>.
    /// </summary>
    private async Task<(CatalogActorType ActorType, Guid? ActorId)> ResolveActorAsync(
        CancellationToken cancellationToken)
    {
        var principal = httpContextAccessor.HttpContext?.User;
        var sub = principal?.FindFirst("sub")?.Value
            ?? principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrWhiteSpace(sub))
        {
            return (CatalogActorType.System, null);
        }

        var userId = await dbContext.Users
            .AsNoTracking()
            .Where(u => u.SupabaseAuthId == sub)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return userId is null
            ? (CatalogActorType.System, null)
            : (CatalogActorType.B2BUser, userId);
    }
}
