using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Notifications;
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
    ICatalogNotificationPublisher notificationPublisher,
    INotificationOutboxScheduler outboxScheduler) : ICatalogOrderService
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

        return list.Select(MapOrder).ToList();
    }

    public async Task<CatalogOrderResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        EnsureTenant();
        var order = await dbContext.CatalogOrders
            .AsNoTracking()
            .Include(o => o.Items)
            .Include(o => o.History)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        return order is null ? null : MapOrder(order);
    }

    public Task<CatalogOrderResponse> ApproveAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(
            id,
            CatalogEventTypes.OrderApproved,
            (order, actorType, actorId) => order.Approve(actorType, actorId),
            cancellationToken);

    public Task<CatalogOrderResponse> RejectAsync(
        Guid id,
        string? reason,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            id,
            CatalogEventTypes.OrderRejected,
            (order, actorType, actorId) => order.Reject(reason ?? string.Empty, actorType, actorId),
            cancellationToken);

    public Task<CatalogOrderResponse> StartPreparingAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(
            id,
            CatalogEventTypes.OrderPreparing,
            (order, actorType, actorId) => order.StartPreparing(actorType, actorId),
            cancellationToken);

    public Task<CatalogOrderResponse> MarkReadyAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(
            id,
            CatalogEventTypes.OrderReady,
            (order, actorType, actorId) => order.MarkReady(actorType, actorId),
            cancellationToken);

    public Task<CatalogOrderResponse> CompleteAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(
            id,
            eventType: null,
            (order, actorType, actorId) => order.Complete(actorType, actorId),
            cancellationToken);

    public Task<CatalogOrderResponse> CancelAsync(
        Guid id,
        string? reason,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            id,
            CatalogEventTypes.OrderCancelledBySupplier,
            (order, actorType, actorId) => order.Cancel(CatalogActorType.B2BUser, reason, actorId),
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
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        var order = await dbContext.CatalogOrders
            .Include(o => o.Items)
            .Include(o => o.History)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Order not found.");

        var actorId = await ResolveB2BUserIdAsync(cancellationToken);
        var actorType = actorId is null ? CatalogActorType.System : CatalogActorType.B2BUser;

        await notificationPublisher.EnsureReadyAsync(cancellationToken);

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            mutate(order, actorType, actorId);

            IReadOnlyList<Guid> queued = [];
            if (eventType is not null)
            {
                queued = await notificationPublisher.PublishOrderEventAsync(
                    order,
                    eventType,
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

            return MapOrder(order);
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

    private async Task<Guid?> ResolveB2BUserIdAsync(CancellationToken cancellationToken)
    {
        var principal = httpContextAccessor.HttpContext?.User;
        var sub = principal?.FindFirst("sub")?.Value
            ?? principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrWhiteSpace(sub))
        {
            return null;
        }

        return await dbContext.Users
            .AsNoTracking()
            .Where(u => u.SupabaseAuthId == sub)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);
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
                    i.SubTotal))
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
                .ToList());

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
