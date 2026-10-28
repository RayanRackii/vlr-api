using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Platform.Api.DomainEvents;
using Platform.Api.Authorization;
using Platform.Api.Notifications;
using Platform.Core.Domain.Events;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using NotificationEntity = Platform.Core.Domain.Entities.Notification;

namespace Platform.Api.Modules.Catalog.Services;

public interface ICatalogNotificationPublisher : IDomainEventHandler<CatalogOrderEvent>
{
    Task<IReadOnlyList<Guid>> IDomainEventHandler<CatalogOrderEvent>.HandleAsync(
        CatalogOrderEvent domainEvent, CancellationToken cancellationToken) =>
        PublishOrderEventAsync(domainEvent.Order, domainEvent.EventType, cancellationToken);

    Task<IReadOnlyList<Guid>> PublishOrderEventAsync(
        CatalogOrder order,
        string eventType,
        CancellationToken cancellationToken = default);
}

public sealed class CatalogNotificationPublisher : ICatalogNotificationPublisher
{
    private readonly AppDbContext dbContext;
    private readonly ITenantProvider tenantProvider;
    private readonly ILogger<CatalogNotificationPublisher> logger;
    private readonly IStaffRecipientResolver staffRecipients;

    public CatalogNotificationPublisher(
        AppDbContext dbContext,
        ITenantProvider tenantProvider,
        IPermissionResolver permissionResolver,
        ILogger<CatalogNotificationPublisher> logger,
        IStaffRecipientResolver? staffRecipients = null)
    {
        this.dbContext = dbContext;
        this.tenantProvider = tenantProvider;
        this.logger = logger;
        // Fall back to the shared resolver built on the same permission resolver so tests that
        // construct this publisher directly keep working without wiring extra services.
        this.staffRecipients = staffRecipients ?? new StaffRecipientResolver(dbContext, permissionResolver);
    }

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
        if (tenantProvider.TenantId is not Guid tenantId)
        {
            logger.LogWarning(
                "Catalog order notification skipped because the ambient tenant is missing. EventType={EventType}",
                eventType);
            return [];
        }

        if (order.TenantId != tenantId)
        {
            logger.LogWarning(
                "Catalog order notification skipped because the order tenant does not match the ambient tenant. EventType={EventType}",
                eventType);
            return [];
        }

        if (eventType == CatalogEventTypes.OrderPreparing)
        {
            return [];
        }

        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

        var payload = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["tenantName"] = tenant?.TradeName is { Length: > 0 } trade ? trade : tenant?.LegalName,
            ["customerName"] = order.CustomerNameSnapshot,
            ["orderNumber"] = $"#{order.OrderNumber}",
            ["orderStatus"] = NotificationCustomerCopy.Label(order.Status),
            ["rejectionReason"] = order.RejectedReason,
            ["cancellationReason"] = order.CancelledReason,
        };

        var configs = await dbContext.TenantNotificationChannelConfigs
            .Where(c => c.TenantId == tenantId && c.EventType == eventType && c.IsActive)
            .ToListAsync(cancellationToken);

        foreach (var local in dbContext.TenantNotificationChannelConfigs.Local
                     .Where(c => c.TenantId == tenantId && c.EventType == eventType && c.IsActive))
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

        // Customer gets every active channel; staff only get the in-app inbox entry. Resolving staff
        // through the shared resolver means every order transition (not just creation) can notify
        // the back office, with the required permission declared once in the capability registry.
        var customer = ResolveCustomerRecipient(order, eventType);
        var staff = await ResolveStaffRecipientsAsync(tenantId, eventType, cancellationToken);

        if (customer is null && staff.Count == 0)
        {
            return [];
        }

        var notification = new NotificationEntity
        {
            TenantId = tenantId,
            EventType = eventType,
            AggregateType = "CatalogOrder",
            AggregateId = order.Id,
            Payload = payload,
        };

        var queuedExternal = new List<Guid>();

        if (customer is not null)
        {
            foreach (var config in configs)
            {
                var delivery = new NotificationDelivery
                {
                    TenantId = tenantId,
                    NotificationId = notification.Id,
                    Channel = config.Channel,
                    RecipientKind = NotificationRecipientKind.Customer,
                    RecipientId = customer.Id,
                    RecipientName = customer.Name,
                    RecipientEmail = customer.Email,
                    RecipientPhone = customer.Phone,
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

        if (staff.Count > 0 && configs.Any(c => c.Channel == NotificationChannel.InApp))
        {
            foreach (var recipient in staff)
            {
                var delivery = new NotificationDelivery
                {
                    TenantId = tenantId,
                    NotificationId = notification.Id,
                    Channel = NotificationChannel.InApp,
                    RecipientKind = NotificationRecipientKind.B2BUser,
                    RecipientId = recipient.UserId,
                    RecipientName = recipient.FullName,
                    RecipientEmail = recipient.Email,
                    Status = NotificationDeliveryStatus.Queued,
                    NextAttemptAt = DateTimeOffset.UtcNow,
                };
                delivery.MarkDelivered();
                notification.AddDelivery(delivery);
            }
        }

        dbContext.Notifications.Add(notification);
        return queuedExternal;
    }

    private static Recipient? ResolveCustomerRecipient(CatalogOrder order, string eventType) =>
        eventType is CatalogEventTypes.OrderCreated
            or CatalogEventTypes.OrderApproved
            or CatalogEventTypes.OrderReady
            or CatalogEventTypes.OrderRejected
            or CatalogEventTypes.OrderCancelledBySupplier
            ? new Recipient(
                NotificationRecipientKind.Customer,
                order.CustomerId,
                order.CustomerNameSnapshot,
                order.CustomerEmailSnapshot,
                order.CustomerPhoneSnapshot)
            : null;

    private async Task<IReadOnlyList<StaffRecipient>> ResolveStaffRecipientsAsync(
        Guid tenantId,
        string eventType,
        CancellationToken cancellationToken)
    {
        if (!NotificationChannelCapability.TryGetEvent(eventType, out var capability)
            || capability.StaffPermission is not { Length: > 0 } permission)
        {
            return [];
        }

        return await staffRecipients.ResolveAsync(tenantId, permission, cancellationToken);
    }

    private async Task EnsurePlatformTemplatesAsync(CancellationToken cancellationToken)
    {
        await NotificationTemplateReconciler.ReconcileAsync(
            dbContext,
            CatalogNotificationTemplates.Seeds.Select(seed => seed.ToEntity()),
            cancellationToken);
    }

    private async Task EnsureTenantChannelDefaultsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var existing = await dbContext.TenantNotificationChannelConfigs
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .Select(c => new { c.EventType, c.Channel })
            .ToListAsync(cancellationToken);

        var existingSet = existing
            .Select(c => $"{c.EventType}|{c.Channel}")
            .ToHashSet(StringComparer.Ordinal);

        foreach (var local in dbContext.TenantNotificationChannelConfigs.Local.Where(c => c.TenantId == tenantId))
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
                (entry.State == EntityState.Added
                 && (entry.Entity is NotificationTemplate or TenantNotificationChannelConfig))
                || (entry.State == EntityState.Modified
                    && entry.Entity is NotificationTemplate)))
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
        new(CatalogEventTypes.OrderCreated, NotificationChannel.WhatsApp, "Pedido {{orderNumber}} recebido.", WhatsAppTemplateName: "catalog_order_status_update"),
        new(CatalogEventTypes.OrderApproved, NotificationChannel.InApp, "Pedido {{orderNumber}} aprovado."),
        new(CatalogEventTypes.OrderApproved, NotificationChannel.Email, "Olá {{customerName}}, o pedido {{orderNumber}} foi aprovado.", "Pedido {{orderNumber}} aprovado"),
        new(CatalogEventTypes.OrderApproved, NotificationChannel.WhatsApp, "Pedido {{orderNumber}} aprovado.", WhatsAppTemplateName: "catalog_order_status_update"),
        new(CatalogEventTypes.OrderReady, NotificationChannel.InApp, "Pedido {{orderNumber}} está pronto."),
        new(CatalogEventTypes.OrderReady, NotificationChannel.Email, "Olá {{customerName}}, o pedido {{orderNumber}} está pronto.", "Pedido {{orderNumber}} pronto"),
        new(CatalogEventTypes.OrderReady, NotificationChannel.WhatsApp, "Pedido {{orderNumber}} está pronto.", WhatsAppTemplateName: "catalog_order_status_update"),
        new(CatalogEventTypes.OrderRejected, NotificationChannel.InApp, "Pedido {{orderNumber}} recusado: {{rejectionReason}}"),
        new(CatalogEventTypes.OrderRejected, NotificationChannel.Email, "Olá {{customerName}}, o pedido {{orderNumber}} foi recusado. Motivo: {{rejectionReason}}", "Pedido {{orderNumber}} recusado"),
        new(CatalogEventTypes.OrderRejected, NotificationChannel.WhatsApp, "Pedido {{orderNumber}} recusado: {{rejectionReason}}", WhatsAppTemplateName: "catalog_order_status_update"),
        new(CatalogEventTypes.OrderCancelledBySupplier, NotificationChannel.InApp, "Pedido {{orderNumber}} cancelado: {{cancellationReason}}"),
        new(CatalogEventTypes.OrderCancelledBySupplier, NotificationChannel.Email, "Olá {{customerName}}, o pedido {{orderNumber}} foi cancelado. Motivo: {{cancellationReason}}", "Pedido {{orderNumber}} cancelado"),
        new(CatalogEventTypes.OrderCancelledBySupplier, NotificationChannel.WhatsApp, "Pedido {{orderNumber}} cancelado: {{cancellationReason}}", WhatsAppTemplateName: "catalog_order_status_update"),
        // Customer-initiated cancellation. Staff-only in-app notice; external channels seeded for completeness.
        new(CatalogEventTypes.OrderCancelledByCustomer, NotificationChannel.InApp, "Pedido {{orderNumber}} cancelado pelo cliente."),
        new(CatalogEventTypes.OrderCancelledByCustomer, NotificationChannel.Email, "O pedido {{orderNumber}} foi cancelado pelo cliente {{customerName}}.", "Pedido {{orderNumber}} cancelado pelo cliente"),
        new(CatalogEventTypes.OrderCancelledByCustomer, NotificationChannel.WhatsApp, "Pedido {{orderNumber}} cancelado pelo cliente.", WhatsAppTemplateName: "catalog_order_status_update"),
    ];
}
