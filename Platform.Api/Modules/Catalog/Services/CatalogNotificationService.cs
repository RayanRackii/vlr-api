using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Notifications;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Catalog.Services;

public interface ICatalogNotificationService
{
    Task<IReadOnlyList<CatalogNotificationDeliveryResponse>> ListAsync(
        CatalogNotificationListQuery query,
        CancellationToken cancellationToken);

    Task<CatalogNotificationDeliveryResponse> ResendAsync(
        Guid deliveryId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CatalogChannelConfigItem>> ListChannelConfigsAsync(
        CancellationToken cancellationToken);

    Task<CatalogChannelConfigItem> UpsertChannelConfigAsync(
        UpsertCatalogChannelConfigRequest request,
        CancellationToken cancellationToken);
}

public sealed class CatalogNotificationService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    ICatalogNotificationPublisher publisher,
    INotificationOutboxProcessor outboxProcessor) : ICatalogNotificationService
{
    public async Task<IReadOnlyList<CatalogNotificationDeliveryResponse>> ListAsync(
        CatalogNotificationListQuery query,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        await EnsureDefaultsAsync(cancellationToken);

        var deliveries = dbContext.NotificationDeliveries
            .AsNoTracking()
            .Include(d => d.Notification)
            .AsQueryable();

        if (query.From is { } from)
        {
            deliveries = deliveries.Where(d => d.CreatedAt >= from);
        }

        if (query.To is { } to)
        {
            deliveries = deliveries.Where(d => d.CreatedAt <= to);
        }

        if (!string.IsNullOrWhiteSpace(query.EventType))
        {
            deliveries = deliveries.Where(d => d.Notification.EventType == query.EventType);
        }

        if (query.RecipientKind is { } kind)
        {
            deliveries = deliveries.Where(d => d.RecipientKind == kind);
        }

        if (query.Channel is { } channel)
        {
            deliveries = deliveries.Where(d => d.Channel == channel);
        }

        if (query.Status is { } status)
        {
            deliveries = deliveries.Where(d => d.Status == status);
        }

        var list = await deliveries
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(cancellationToken);

        return list.Select(Map).ToList();
    }

    public async Task<CatalogNotificationDeliveryResponse> ResendAsync(
        Guid deliveryId,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        var delivery = await dbContext.NotificationDeliveries
            .Include(d => d.Notification)
            .Include(d => d.Attempts)
            .FirstOrDefaultAsync(d => d.Id == deliveryId, cancellationToken)
            ?? throw new KeyNotFoundException("Delivery not found.");

        delivery.ResetForResend();
        await dbContext.SaveChangesAsync(cancellationToken);
        await outboxProcessor.ProcessDeliveryAsync(delivery.Id, cancellationToken);

        var reloaded = await dbContext.NotificationDeliveries
            .Include(d => d.Notification)
            .FirstAsync(d => d.Id == delivery.Id, cancellationToken);
        return Map(reloaded);
    }

    public async Task<IReadOnlyList<CatalogChannelConfigItem>> ListChannelConfigsAsync(
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        await EnsureDefaultsAsync(cancellationToken);

        var configs = await dbContext.TenantNotificationChannelConfigs
            .AsNoTracking()
            .OrderBy(c => c.EventType)
            .ThenBy(c => c.Channel)
            .ToListAsync(cancellationToken);

        return configs
            .Select(c => new CatalogChannelConfigItem(c.EventType, c.Channel, c.IsActive))
            .ToList();
    }

    public async Task<CatalogChannelConfigItem> UpsertChannelConfigAsync(
        UpsertCatalogChannelConfigRequest request,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        if (request.Channel == NotificationChannel.Sms && request.IsActive)
        {
            throw new ArgumentException("SMS channel is not available.");
        }

        if (!CatalogEventTypes.Notifying.Contains(request.EventType))
        {
            throw new ArgumentException("Unknown catalog notification event.");
        }

        await EnsureDefaultsAsync(cancellationToken);

        var config = await dbContext.TenantNotificationChannelConfigs
            .FirstOrDefaultAsync(
                c => c.EventType == request.EventType && c.Channel == request.Channel,
                cancellationToken);

        if (config is null)
        {
            config = new TenantNotificationChannelConfig
            {
                TenantId = EnsureTenant(),
                EventType = request.EventType,
                Channel = request.Channel,
                IsActive = request.IsActive,
            };
            dbContext.TenantNotificationChannelConfigs.Add(config);
        }
        else
        {
            config.IsActive = request.IsActive;
            config.Touch();
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return new CatalogChannelConfigItem(config.EventType, config.Channel, config.IsActive);
    }

    private async Task EnsureDefaultsAsync(CancellationToken cancellationToken)
    {
        EnsureTenant();
        await publisher.EnsureReadyAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private Guid EnsureTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");

    private static CatalogNotificationDeliveryResponse Map(NotificationDelivery delivery) =>
        new(
            delivery.Id,
            delivery.NotificationId,
            delivery.Notification.EventType,
            delivery.Channel,
            delivery.RecipientKind,
            delivery.RecipientId,
            delivery.RecipientName,
            delivery.Status,
            delivery.AttemptCount,
            delivery.ErrorMessage,
            delivery.CreatedAt);
}
