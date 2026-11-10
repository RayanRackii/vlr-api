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
    INotificationDeliveryService deliveries) : ICatalogNotificationService
{
    public async Task<IReadOnlyList<CatalogNotificationDeliveryResponse>> ListAsync(
        CatalogNotificationListQuery query,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        await EnsureDefaultsAsync(cancellationToken);
        var items = await deliveries.ListAllAsync(
            new NotificationDeliveryFilter(
                query.From,
                query.To,
                query.EventType,
                query.RecipientKind,
                query.Channel,
                query.Status),
            cancellationToken);
        return items.Select(ToCatalog).ToList();
    }

    public async Task<CatalogNotificationDeliveryResponse> ResendAsync(
        Guid deliveryId,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        return ToCatalog(await deliveries.ResendAsync(deliveryId, cancellationToken));
    }

    public async Task<IReadOnlyList<CatalogChannelConfigItem>> ListChannelConfigsAsync(
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        await EnsureDefaultsAsync(cancellationToken);

        var notifying = CatalogEventTypes.Notifying;
        var configs = await dbContext.TenantNotificationChannelConfigs
            .AsNoTracking()
            .Where(c => notifying.Contains(c.EventType))
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

    private static CatalogNotificationDeliveryResponse ToCatalog(NotificationDeliveryResponse delivery) =>
        new(
            delivery.Id,
            delivery.NotificationId,
            delivery.EventKey,
            delivery.Channel,
            delivery.RecipientKind,
            delivery.RecipientId,
            delivery.RecipientName,
            delivery.Status,
            delivery.AttemptCount,
            delivery.ErrorMessage,
            delivery.CreatedAt,
            delivery.CanResend,
            delivery.Attempts
                .Select(attempt => new CatalogNotificationAttemptResponse(
                    attempt.AttemptNumber,
                    attempt.StartedAt,
                    attempt.FinishedAt,
                    attempt.Outcome,
                    attempt.ErrorMessage,
                    attempt.Channel))
                .ToList());
}
