using Microsoft.EntityFrameworkCore;
using Platform.Api.Authorization;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using NotificationEntity = Platform.Core.Domain.Entities.Notification;

namespace Platform.Api.Modules.Catalog.Services;

public interface ICatalogNotificationPublisher
{
    Task EnsureReadyAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> PublishOrderEventAsync(
        CatalogOrder order,
        string eventType,
        CancellationToken cancellationToken = default);
}

public sealed class CatalogNotificationPublisher(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IPermissionResolver permissionResolver) : ICatalogNotificationPublisher
{
    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");

        await EnsurePlatformTemplatesAsync(cancellationToken);
        await EnsureTenantChannelDefaultsAsync(tenantId, cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> PublishOrderEventAsync(
        CatalogOrder order,
        string eventType,
        CancellationToken cancellationToken = default)
    {
        if (eventType == CatalogEventTypes.OrderPreparing)
        {
            return [];
        }

        await EnsureReadyAsync(cancellationToken);

        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == order.TenantId, cancellationToken);

        var payload = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["tenantName"] = tenant?.TradeName is { Length: > 0 } trade ? trade : tenant?.LegalName,
            ["customerName"] = order.CustomerNameSnapshot,
            ["orderNumber"] = $"#{order.OrderNumber}",
            ["orderStatus"] = order.Status.ToString(),
            ["rejectionReason"] = order.RejectedReason,
            ["cancellationReason"] = order.CancelledReason,
        };

        var configs = await dbContext.TenantNotificationChannelConfigs
            .Where(c => c.EventType == eventType && c.IsActive)
            .ToListAsync(cancellationToken);

        foreach (var local in dbContext.TenantNotificationChannelConfigs.Local
                     .Where(c => c.EventType == eventType && c.IsActive))
        {
            if (configs.All(c => c.Id != local.Id))
            {
                configs.Add(local);
            }
        }

        if (configs.Count == 0)
        {
            return [];
        }

        var recipients = await ResolveRecipientsAsync(order, eventType, cancellationToken);
        if (recipients.Count == 0)
        {
            return [];
        }

        var notification = new NotificationEntity
        {
            TenantId = order.TenantId,
            EventType = eventType,
            AggregateType = "CatalogOrder",
            AggregateId = order.Id,
            Payload = payload,
        };

        var queuedExternal = new List<Guid>();

        foreach (var recipient in recipients)
        {
            foreach (var config in configs)
            {
                var delivery = new NotificationDelivery
                {
                    TenantId = order.TenantId,
                    NotificationId = notification.Id,
                    Channel = config.Channel,
                    RecipientKind = recipient.Kind,
                    RecipientId = recipient.Id,
                    RecipientName = recipient.Name,
                    RecipientEmail = recipient.Email,
                    RecipientPhone = recipient.Phone,
                    Status = NotificationDeliveryStatus.Queued,
                    NextAttemptAt = DateTimeOffset.UtcNow,
                };

                if (config.Channel == NotificationChannel.InApp)
                {
                    delivery.MarkDelivered();
                }
                else
                {
                    queuedExternal.Add(delivery.Id);
                }

                notification.AddDelivery(delivery);
            }
        }

        dbContext.Notifications.Add(notification);
        return queuedExternal;
    }

    private async Task<IReadOnlyList<Recipient>> ResolveRecipientsAsync(
        CatalogOrder order,
        string eventType,
        CancellationToken cancellationToken)
    {
        var list = new List<Recipient>();

        if (eventType is CatalogEventTypes.OrderCreated
            or CatalogEventTypes.OrderApproved
            or CatalogEventTypes.OrderReady
            or CatalogEventTypes.OrderRejected
            or CatalogEventTypes.OrderCancelledBySupplier)
        {
            list.Add(new Recipient(
                NotificationRecipientKind.Customer,
                order.CustomerId,
                order.CustomerNameSnapshot,
                order.CustomerEmailSnapshot,
                order.CustomerPhoneSnapshot));
        }

        if (eventType == CatalogEventTypes.OrderCreated)
        {
            var tenantId = tenantProvider.TenantId ?? order.TenantId;
            var users = await dbContext.Users
                .AsNoTracking()
                .Where(u => u.IsActive)
                .ToListAsync(cancellationToken);

            foreach (var user in users)
            {
                if (await permissionResolver.HasPermissionAsync(
                    tenantId,
                    user.Id,
                    Permissions.Catalog.OrdersManage,
                    cancellationToken))
                {
                    list.Add(new Recipient(
                        NotificationRecipientKind.B2BUser,
                        user.Id,
                        user.FullName,
                        user.Email,
                        Phone: null));
                }
            }
        }

        return list;
    }

    private async Task EnsurePlatformTemplatesAsync(CancellationToken cancellationToken)
    {
        var existing = await dbContext.NotificationTemplates
            .Select(t => new { t.EventType, t.Channel, t.Language })
            .ToListAsync(cancellationToken);

        var existingSet = existing
            .Select(t => $"{t.EventType}|{t.Channel}|{t.Language}")
            .ToHashSet(StringComparer.Ordinal);

        foreach (var seed in CatalogNotificationTemplates.Seeds)
        {
            var key = $"{seed.EventType}|{seed.Channel}|{seed.Language}";
            if (existingSet.Contains(key))
            {
                continue;
            }

            dbContext.NotificationTemplates.Add(seed);
            existingSet.Add(key);
        }
    }

    private async Task EnsureTenantChannelDefaultsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var existing = await dbContext.TenantNotificationChannelConfigs
            .Select(c => new { c.EventType, c.Channel })
            .ToListAsync(cancellationToken);

        var existingSet = existing
            .Select(c => $"{c.EventType}|{c.Channel}")
            .ToHashSet(StringComparer.Ordinal);

        foreach (var eventType in CatalogEventTypes.Notifying)
        {
            foreach (var channel in new[]
                     {
                         NotificationChannel.InApp,
                         NotificationChannel.Email,
                         NotificationChannel.WhatsApp,
                         NotificationChannel.Sms,
                     })
            {
                var key = $"{eventType}|{channel}";
                if (existingSet.Contains(key))
                {
                    continue;
                }

                dbContext.TenantNotificationChannelConfigs.Add(new TenantNotificationChannelConfig
                {
                    TenantId = tenantId,
                    EventType = eventType,
                    Channel = channel,
                    IsActive = channel == NotificationChannel.InApp,
                });
                existingSet.Add(key);
            }
        }
    }

    private sealed record Recipient(
        NotificationRecipientKind Kind,
        Guid Id,
        string Name,
        string? Email,
        string? Phone);
}

internal static class CatalogNotificationTemplates
{
    public static IReadOnlyList<NotificationTemplate> Seeds { get; } =
    [
        InApp(CatalogEventTypes.OrderCreated, "Pedido {{orderNumber}} recebido."),
        Email(CatalogEventTypes.OrderCreated, "Pedido {{orderNumber}}", "Olá {{customerName}}, recebemos o pedido {{orderNumber}}."),
        WhatsApp(CatalogEventTypes.OrderCreated, "catalog_order_created", "Pedido {{orderNumber}} recebido."),
        InApp(CatalogEventTypes.OrderApproved, "Pedido {{orderNumber}} aprovado."),
        Email(CatalogEventTypes.OrderApproved, "Pedido {{orderNumber}} aprovado", "Olá {{customerName}}, o pedido {{orderNumber}} foi aprovado."),
        WhatsApp(CatalogEventTypes.OrderApproved, "catalog_order_approved", "Pedido {{orderNumber}} aprovado."),
        InApp(CatalogEventTypes.OrderReady, "Pedido {{orderNumber}} está pronto."),
        Email(CatalogEventTypes.OrderReady, "Pedido {{orderNumber}} pronto", "Olá {{customerName}}, o pedido {{orderNumber}} está pronto."),
        WhatsApp(CatalogEventTypes.OrderReady, "catalog_order_ready", "Pedido {{orderNumber}} está pronto."),
        InApp(CatalogEventTypes.OrderRejected, "Pedido {{orderNumber}} recusado: {{rejectionReason}}"),
        Email(CatalogEventTypes.OrderRejected, "Pedido {{orderNumber}} recusado", "Olá {{customerName}}, o pedido {{orderNumber}} foi recusado. Motivo: {{rejectionReason}}"),
        WhatsApp(CatalogEventTypes.OrderRejected, "catalog_order_rejected", "Pedido {{orderNumber}} recusado: {{rejectionReason}}"),
        InApp(CatalogEventTypes.OrderCancelledBySupplier, "Pedido {{orderNumber}} cancelado: {{cancellationReason}}"),
        Email(CatalogEventTypes.OrderCancelledBySupplier, "Pedido {{orderNumber}} cancelado", "Olá {{customerName}}, o pedido {{orderNumber}} foi cancelado. Motivo: {{cancellationReason}}"),
        WhatsApp(CatalogEventTypes.OrderCancelledBySupplier, "catalog_order_cancelled", "Pedido {{orderNumber}} cancelado: {{cancellationReason}}"),
    ];

    private static NotificationTemplate InApp(string eventType, string body) =>
        new()
        {
            EventType = eventType,
            Channel = NotificationChannel.InApp,
            Language = "pt-BR",
            BodyTemplate = body,
            IsActive = true,
        };

    private static NotificationTemplate Email(string eventType, string subject, string body) =>
        new()
        {
            EventType = eventType,
            Channel = NotificationChannel.Email,
            Language = "pt-BR",
            SubjectTemplate = subject,
            BodyTemplate = body,
            IsActive = true,
        };

    private static NotificationTemplate WhatsApp(string eventType, string templateName, string body) =>
        new()
        {
            EventType = eventType,
            Channel = NotificationChannel.WhatsApp,
            Language = "pt-BR",
            BodyTemplate = body,
            WhatsAppTemplateName = templateName,
            IsActive = true,
        };
}
