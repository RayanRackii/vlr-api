using Microsoft.EntityFrameworkCore;
using Platform.Api.DomainEvents;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Rentals.Contracts;
using Platform.Api.Notifications;
using Platform.Core.Domain.Events;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Domain.Exceptions;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Catalog.Services;

public interface ICatalogOrderService
{
    Task<IReadOnlyList<CatalogOrderResponse>> ListAsync(
        CatalogOrderListQuery query,
        CancellationToken cancellationToken);

    Task<CatalogOrderResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<CatalogOrderResponse> ApproveAsync(Guid id, CancellationToken cancellationToken);

    Task<CatalogOrderResponse> ApproveAsync(
        Guid id,
        CatalogOrderApprovePlanningRequest? planningRequest,
        CancellationToken cancellationToken);

    Task<CatalogOrderResponse> RejectAsync(Guid id, string? reason, CancellationToken cancellationToken);

    Task<CatalogOrderResponse> StartPreparingAsync(Guid id, CancellationToken cancellationToken);

    Task<CatalogOrderResponse> MarkReadyAsync(Guid id, CancellationToken cancellationToken);

    Task<CatalogOrderResponse> CompleteAsync(Guid id, CancellationToken cancellationToken);

    Task<CatalogOrderResponse> CancelAsync(Guid id, string? reason, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProductRequestResponse>> ListProductRequestsAsync(
        CancellationToken cancellationToken);

    Task<ProductRequestResponse?> GetProductRequestAsync(Guid id, CancellationToken cancellationToken);
}

public sealed class CatalogOrderService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IHttpContextAccessor httpContextAccessor,
    IDomainEventDispatcher<CatalogOrderEvent> domainEvents,
    INotificationOutboxScheduler outboxScheduler,
    ICatalogOrderAllocationService allocationService,
    IResourceOccupancyService occupancy) : ICatalogOrderService
{
    public async Task<IReadOnlyList<CatalogOrderResponse>> ListAsync(
        CatalogOrderListQuery query,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        var orders = dbContext.CatalogOrders
            .AsNoTracking()
            .Include(o => o.Items)
            .Include(o => o.History)
            .AsQueryable();

        if (query.OrderNumber is { } number)
        {
            orders = orders.Where(o => o.OrderNumber == number);
        }

        if (query.Status is { } status)
        {
            orders = orders.Where(o => o.Status == status);
        }

        if (query.CustomerId is { } customerId)
        {
            orders = orders.Where(o => o.CustomerId == customerId);
        }

        if (query.From is { } from)
        {
            orders = orders.Where(o => o.CreatedAt >= from);
        }

        if (query.To is { } to)
        {
            orders = orders.Where(o => o.CreatedAt <= to);
        }

        var list = await orders
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(cancellationToken);

        return await CatalogOrderPlanningMapper.EnrichAsync(dbContext, list, includeAllocationDetail: true, cancellationToken);
    }

    public async Task<CatalogOrderResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        EnsureTenant();
        var order = await dbContext.CatalogOrders
            .AsNoTracking()
            .Include(o => o.Items)
            .Include(o => o.History)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        return order is null
            ? null
            : await CatalogOrderPlanningMapper.EnrichOneAsync(dbContext, order, includeAllocationDetail: true, cancellationToken);
    }

    public Task<CatalogOrderResponse> ApproveAsync(Guid id, CancellationToken cancellationToken) =>
        ApproveAsync(id, planningRequest: null, cancellationToken);

    public Task<CatalogOrderResponse> ApproveAsync(
        Guid id,
        CatalogOrderApprovePlanningRequest? planningRequest,
        CancellationToken cancellationToken) =>
        allocationService.ApproveWithPlanningAsync(id, planningRequest, cancellationToken);

    public Task<CatalogOrderResponse> RejectAsync(
        Guid id,
        string? reason,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            id,
            CatalogEventTypes.OrderRejected,
            (order, actorType, actorId) => order.Reject(reason ?? string.Empty, actorType, actorId),
            planningHook: (db, order, ct) => CatalogAllocationExecutionService.ReleaseUnstartedAllocationsHook(occupancy, db, order, ct),
            cancellationToken);

    public Task<CatalogOrderResponse> StartPreparingAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(
            id,
            CatalogEventTypes.OrderPreparing,
            (order, actorType, actorId) => order.StartPreparing(actorType, actorId),
            // Existing `preparing` action also drives the internal execution operation: any pending
            // (Scheduled) allocation starts running at the same moment the order is marked Preparing
            // (order-equipment-link-codex §5: "Existing preparing and ready actions invoke the
            // internal execution operations"). Staff can still call the standalone usage endpoint
            // out-of-band (e.g. production actually starts later than the commercial status).
            planningHook: (db, order, ct) => CatalogAllocationExecutionService.StartPendingAllocationHook(occupancy, db, order, ct),
            cancellationToken);

    public Task<CatalogOrderResponse> MarkReadyAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(
            id,
            CatalogEventTypes.OrderReady,
            (order, actorType, actorId) => order.MarkReady(actorType, actorId),
            // Ready requires work already finished (allocation at least Cleaning) across every
            // required item; the hook throws InvalidCatalogOrderTransitionException otherwise so the
            // whole transition rolls back before the commercial status is touched.
            planningHook: (db, order, ct) => CatalogAllocationExecutionService.RequireWorkFinishedHook(occupancy, db, order, ct),
            cancellationToken);

    public Task<CatalogOrderResponse> CompleteAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(
            id,
            eventType: null,
            (order, actorType, actorId) => order.Complete(actorType, actorId),
            planningHook: null,
            cancellationToken);

    public Task<CatalogOrderResponse> CancelAsync(
        Guid id,
        string? reason,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            id,
            CatalogEventTypes.OrderCancelledBySupplier,
            (order, actorType, actorId) => order.Cancel(CatalogActorType.B2BUser, reason, actorId),
            // Unstarted (Scheduled) allocations are released transactionally on cancel/reject; an
            // already-started (Running/Cleaning) allocation keeps the resource occupied until
            // explicit release via the usage endpoint, even though the order is now terminal.
            planningHook: (db, order, ct) => CatalogAllocationExecutionService.ReleaseUnstartedAllocationsHook(occupancy, db, order, ct),
            cancellationToken);

    public async Task<IReadOnlyList<ProductRequestResponse>> ListProductRequestsAsync(
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        var requests = await dbContext.ProductRequests
            .AsNoTracking()
            .Include(r => r.Files)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);

        return requests.Select(MapProductRequest).ToList();
    }

    public async Task<ProductRequestResponse?> GetProductRequestAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        var request = await dbContext.ProductRequests
            .AsNoTracking()
            .Include(r => r.Files)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        return request is null ? null : MapProductRequest(request);
    }

    private async Task<CatalogOrderResponse> TransitionAsync(
        Guid id,
        string? eventType,
        Action<CatalogOrder, CatalogActorType, Guid?> mutate,
        Func<AppDbContext, CatalogOrder, CancellationToken, Task>? planningHook,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        var order = await dbContext.CatalogOrders
            .Include(o => o.Items)
            .Include(o => o.History)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Order not found.");

        var (actorType, actorId) = await CatalogOrderActorResolver.ResolveAsync(
            httpContextAccessor, dbContext, cancellationToken);

        await domainEvents.EnsureReadyAsync(cancellationToken);

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            // The planning hook runs before the commercial mutation so a failure (e.g. "ready"
            // without work finished) never touches order.Status — the whole transaction rolls back.
            if (planningHook is not null)
            {
                await planningHook(dbContext, order, cancellationToken);
            }

            mutate(order, actorType, actorId);

            IReadOnlyList<Guid> queued = [];
            if (eventType is not null)
            {
                queued = await domainEvents.DispatchAsync(
                    new CatalogOrderEvent(order, eventType),
                    cancellationToken);
            }

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new InvalidCatalogOrderTransitionException(
                    "The order was modified by another user.");
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

    private Guid EnsureTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");

    internal static CatalogOrderResponse MapOrder(CatalogOrder order) =>
        new(
            order.Id,
            $"#{order.OrderNumber}",
            order.OrderNumber,
            order.Status,
            order.CustomerId,
            order.CustomerNameSnapshot,
            order.CustomerEmailSnapshot,
            order.CustomerPhoneSnapshot,
            order.CustomerNote,
            order.TotalAmount,
            order.Currency,
            order.RejectedReason,
            order.CancelledReason,
            order.CreatedAt,
            order.Items
                .Select(i => new CatalogOrderItemResponse(
                    i.Id,
                    i.ProductId,
                    i.ProductNameSnapshot,
                    i.ProductCodeSnapshot,
                    i.UnitPriceSnapshot,
                    i.Currency,
                    i.Quantity,
                    i.SubTotal,
                    ProductionSummaryStatus.NotRequired,
                    null))
                .ToList(),
            order.History
                .OrderBy(h => h.CreatedAt)
                .Select(h => new CatalogOrderHistoryResponse(
                    h.Id,
                    h.Status,
                    h.ActorType,
                    h.ActorId,
                    h.Reason,
                    h.CreatedAt))
                .ToList(),
            Convert.ToBase64String(order.RowVersion),
            order.PlanningRevision,
            ProductionSummaryStatus.NotRequired,
            null);

    internal static ProductRequestResponse MapProductRequest(ProductRequest request) =>
        new(
            request.Id,
            request.CustomerId,
            request.Description,
            request.Quantity,
            request.Note,
            request.Status,
            request.CreatedAt,
            request.Files
                .Select(f => new ProductRequestFileDto(f.Id, f.FileName, f.MimeType, f.SizeBytes))
                .ToList());
}
