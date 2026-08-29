using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
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

        IDbContextTransaction? seedTransaction = null;
        if (dbContext.Database.IsRelational()
            && dbContext.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true
            && dbContext.Database.CurrentTransaction is null)
        {
            seedTransaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            await dbContext.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock(748201928)",
                cancellationToken);
        }

        try
        {
            await EnsurePlatformTemplatesAsync(cancellationToken);
            await SaveSeedIgnoringUniqueAsync(cancellationToken);
            await EnsureTenantChannelDefaultsAsync(tenantId, cancellationToken);
            await SaveSeedIgnoringUniqueAsync(cancellationToken);
            if (seedTransaction is not null)
            {
                await seedTransaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            if (seedTransaction is not null)
            {
                await seedTransaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
        finally
        {
            if (seedTransaction is not null)
            {
                await seedTransaction.DisposeAsync();
            }
        }
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
            .AsNoTracking()
            .Select(t => new { t.EventType, t.Channel, t.Language })
            .ToListAsync(cancellationToken);

        var existingSet = existing
            .Select(t => $"{t.EventType}|{t.Channel}|{t.Language}")
            .ToHashSet(StringComparer.Ordinal);

        foreach (var local in dbContext.NotificationTemplates.Local)
        {
            existingSet.Add($"{local.EventType}|{local.Channel}|{local.Language}");
        }

        foreach (var seed in CatalogNotificationTemplates.Seeds)
        {
            var key = $"{seed.EventType}|{seed.Channel}|pt-BR";
            if (existingSet.Contains(key))
            {
                continue;
            }

            dbContext.NotificationTemplates.Add(seed.ToEntity());
            existingSet.Add(key);
        }
    }

    private async Task EnsureTenantChannelDefaultsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var existing = await dbContext.TenantNotificationChannelConfigs
            .AsNoTracking()
            .Select(c => new { c.EventType, c.Channel })
            .ToListAsync(cancellationToken);

        var existingSet = existing
            .Select(c => $"{c.EventType}|{c.Channel}")
            .ToHashSet(StringComparer.Ordinal);

        foreach (var local in dbContext.TenantNotificationChannelConfigs.Local)
        {
            existingSet.Add($"{local.EventType}|{local.Channel}");
        }

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

    private async Task SaveSeedIgnoringUniqueAsync(CancellationToken cancellationToken)
    {
        if (!dbContext.ChangeTracker.Entries().Any(entry =>
                entry.State == EntityState.Added
                && (entry.Entity is NotificationTemplate or TenantNotificationChannelConfig)))
        {
            return;
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            foreach (var entry in dbContext.ChangeTracker.Entries()
                         .Where(e => e.State is EntityState.Added or EntityState.Unchanged
                             && (e.Entity is NotificationTemplate or TenantNotificationChannelConfig))
                         .ToList())
            {
                if (entry.State == EntityState.Added)
                {
                    entry.State = EntityState.Detached;
                }
            }
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres
        && postgres.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.DeadlockDetected;

    private sealed record Recipient(
        NotificationRecipientKind Kind,
        Guid Id,
        string Name,
        string? Email,
        string? Phone);
}

internal static class CatalogNotificationTemplates
{
    internal sealed record Seed(
        string EventType,
        NotificationChannel Channel,
        string Body,
        string? Subject = null,
        string? WhatsAppTemplateName = null)
    {
        public NotificationTemplate ToEntity() =>
            new()
            {
                EventType = EventType,
                Channel = Channel,
                Language = "pt-BR",
                SubjectTemplate = Subject,
                BodyTemplate = Body,
                WhatsAppTemplateName = WhatsAppTemplateName,
                IsActive = true,
            };
    }

    public static IReadOnlyList<Seed> Seeds { get; } =
    [
        new(CatalogEventTypes.OrderCreated, NotificationChannel.InApp, "Pedido {{orderNumber}} recebido."),
        new(CatalogEventTypes.OrderCreated, NotificationChannel.Email, "Olá {{customerName}}, recebemos o pedido {{orderNumber}}.", "Pedido {{orderNumber}}"),
        new(CatalogEventTypes.OrderCreated, NotificationChannel.WhatsApp, "Pedido {{orderNumber}} recebido.", WhatsAppTemplateName: "catalog_order_created"),
        new(CatalogEventTypes.OrderApproved, NotificationChannel.InApp, "Pedido {{orderNumber}} aprovado."),
        new(CatalogEventTypes.OrderApproved, NotificationChannel.Email, "Olá {{customerName}}, o pedido {{orderNumber}} foi aprovado.", "Pedido {{orderNumber}} aprovado"),
        new(CatalogEventTypes.OrderApproved, NotificationChannel.WhatsApp, "Pedido {{orderNumber}} aprovado.", WhatsAppTemplateName: "catalog_order_approved"),
        new(CatalogEventTypes.OrderReady, NotificationChannel.InApp, "Pedido {{orderNumber}} está pronto."),
        new(CatalogEventTypes.OrderReady, NotificationChannel.Email, "Olá {{customerName}}, o pedido {{orderNumber}} está pronto.", "Pedido {{orderNumber}} pronto"),
        new(CatalogEventTypes.OrderReady, NotificationChannel.WhatsApp, "Pedido {{orderNumber}} está pronto.", WhatsAppTemplateName: "catalog_order_ready"),
        new(CatalogEventTypes.OrderRejected, NotificationChannel.InApp, "Pedido {{orderNumber}} recusado: {{rejectionReason}}"),
        new(CatalogEventTypes.OrderRejected, NotificationChannel.Email, "Olá {{customerName}}, o pedido {{orderNumber}} foi recusado. Motivo: {{rejectionReason}}", "Pedido {{orderNumber}} recusado"),
        new(CatalogEventTypes.OrderRejected, NotificationChannel.WhatsApp, "Pedido {{orderNumber}} recusado: {{rejectionReason}}", WhatsAppTemplateName: "catalog_order_rejected"),
        new(CatalogEventTypes.OrderCancelledBySupplier, NotificationChannel.InApp, "Pedido {{orderNumber}} cancelado: {{cancellationReason}}"),
        new(CatalogEventTypes.OrderCancelledBySupplier, NotificationChannel.Email, "Olá {{customerName}}, o pedido {{orderNumber}} foi cancelado. Motivo: {{cancellationReason}}", "Pedido {{orderNumber}} cancelado"),
        new(CatalogEventTypes.OrderCancelledBySupplier, NotificationChannel.WhatsApp, "Pedido {{orderNumber}} cancelado: {{cancellationReason}}", WhatsAppTemplateName: "catalog_order_cancelled"),
    ];
}
