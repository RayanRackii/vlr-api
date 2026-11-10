using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Notifications;

public interface INotificationDeliveryService
{
    Task<NotificationDeliveryPageResponse> ListPageAsync(
        NotificationDeliveryFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<NotificationDeliveryResponse>> ListAllAsync(
        NotificationDeliveryFilter filter,
        CancellationToken cancellationToken);

    Task<NotificationDeliveryResponse> ResendAsync(
        Guid deliveryId,
        CancellationToken cancellationToken);
}

public sealed class NotificationDeliveryService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    INotificationOutboxProcessor outboxProcessor) : INotificationDeliveryService
{
    public async Task<NotificationDeliveryPageResponse> ListPageAsync(
        NotificationDeliveryFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        var filtered = Filtered(filter);
        var total = await filtered.CountAsync(cancellationToken);
        var items = await filtered
            .OrderByDescending(delivery => delivery.CreatedAt)
            .ThenByDescending(delivery => delivery.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(delivery => delivery.Notification)
            .Include(delivery => delivery.Attempts)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        return new NotificationDeliveryPageResponse(
            items.Select(Map).ToList(),
            page,
            pageSize,
            total);
    }

    public async Task<IReadOnlyList<NotificationDeliveryResponse>> ListAllAsync(
        NotificationDeliveryFilter filter,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        var items = await Filtered(filter)
            .OrderByDescending(delivery => delivery.CreatedAt)
            .ThenByDescending(delivery => delivery.Id)
            .Include(delivery => delivery.Notification)
            .Include(delivery => delivery.Attempts)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        return items.Select(Map).ToList();
    }

    public async Task<NotificationDeliveryResponse> ResendAsync(
        Guid deliveryId,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        await outboxProcessor.ResendAsync(deliveryId, cancellationToken);
        var reloaded = await dbContext.NotificationDeliveries
            .AsNoTracking()
            .Include(delivery => delivery.Notification)
            .Include(delivery => delivery.Attempts)
            .FirstAsync(delivery => delivery.Id == deliveryId, cancellationToken);
        return Map(reloaded);
    }

    private IQueryable<NotificationDelivery> Filtered(NotificationDeliveryFilter filter)
    {
        var query = dbContext.NotificationDeliveries.AsNoTracking();
        if (filter.From is { } from)
        {
            query = query.Where(delivery => delivery.CreatedAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(delivery => delivery.CreatedAt <= to);
        }

        if (!string.IsNullOrWhiteSpace(filter.EventType))
        {
            query = query.Where(delivery => delivery.Notification.EventType == filter.EventType);
        }

        if (filter.RecipientKind is { } kind)
        {
            query = query.Where(delivery => delivery.RecipientKind == kind);
        }

        if (filter.Channel is { } channel)
        {
            query = query.Where(delivery => delivery.Channel == channel);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(delivery => delivery.Status == status);
        }

        return query;
    }

    private Guid EnsureTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");

    private static NotificationDeliveryResponse Map(NotificationDelivery delivery)
    {
        string? module = NotificationChannelCapability.TryGetEvent(delivery.Notification.EventType, out var capability)
            ? capability.ModuleKey
            : null;
        return new NotificationDeliveryResponse(
            delivery.Id,
            delivery.NotificationId,
            delivery.Notification.EventType,
            module,
            delivery.Channel,
            delivery.RecipientKind,
            delivery.RecipientId,
            delivery.RecipientName,
            delivery.Status,
            delivery.AttemptCount,
            delivery.ErrorMessage,
            delivery.CreatedAt,
            NotificationDeliveryFailures.CanResend(delivery.Status, delivery.Channel, delivery.ErrorMessage),
            delivery.Attempts
                .OrderBy(attempt => attempt.AttemptNumber)
                .Select(attempt => new NotificationDeliveryAttemptResponse(
                    attempt.AttemptNumber,
                    attempt.StartedAt,
                    attempt.FinishedAt,
                    attempt.Outcome,
                    attempt.ErrorMessage,
                    delivery.Channel))
                .ToList());
    }
}
