using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Platform.Api.Notifications;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Time;
using NotificationEntity = Platform.Core.Domain.Entities.Notification;

namespace Platform.Api.Modules.Rentals.Services;

public interface IRentalsNotificationPublisher
{
    Task EnsureReadyAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> PublishReservationEventAsync(
        Reservation reservation,
        string eventType,
        CancellationToken cancellationToken = default);
}

public sealed class NoOpRentalsNotificationPublisher : IRentalsNotificationPublisher
{
    public Task EnsureReadyAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<Guid>> PublishReservationEventAsync(
        Reservation reservation,
        string eventType,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);
}

public sealed class RentalsNotificationPublisher(
    AppDbContext dbContext,
    ITenantProvider tenantProvider) : IRentalsNotificationPublisher
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
                "SELECT pg_advisory_xact_lock(748201929)",
                cancellationToken);
        }

        try
        {
            await NotificationTemplateReconciler.ReconcileAsync(
                dbContext,
                RentalsNotificationTemplates.Seeds.Select(seed => seed.ToEntity()),
                cancellationToken);
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

    public async Task<IReadOnlyList<Guid>> PublishReservationEventAsync(
        Reservation reservation,
        string eventType,
        CancellationToken cancellationToken = default)
    {
        if (!RentalEventTypes.Notifying.Contains(eventType))
        {
            return [];
        }

        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == reservation.TenantId, cancellationToken);

        var payload = BuildPayload(reservation, tenant);

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

        var notification = new NotificationEntity
        {
            TenantId = reservation.TenantId,
            EventType = eventType,
            AggregateType = "Reservation",
            AggregateId = reservation.Id,
            Payload = payload,
        };

        var queuedExternal = new List<Guid>();

        foreach (var config in configs)
        {
            var delivery = new NotificationDelivery
            {
                TenantId = reservation.TenantId,
                NotificationId = notification.Id,
                Channel = config.Channel,
                RecipientKind = NotificationRecipientKind.Customer,
                RecipientId = reservation.CustomerId,
                RecipientName = reservation.CustomerName,
                RecipientEmail = reservation.Customer?.Email,
                RecipientPhone = reservation.CustomerWhatsApp,
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

        dbContext.Notifications.Add(notification);
        return queuedExternal;
    }

    private static Dictionary<string, string?> BuildPayload(
        Reservation reservation,
        Tenant? tenant)
    {
        var tenantName = tenant?.TradeName is { Length: > 0 } trade
            ? trade
            : tenant?.LegalName;
        var reference = FormatReservationReference(reservation);
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["tenantName"] = tenantName,
            ["customerName"] = reservation.CustomerName,
            ["reservationReference"] = reference,
            ["reservationStatus"] = NotificationCustomerCopy.Label(reservation.Status),
            ["reservationDateTime"] = BrazilTimeZone.FormatCivilDateTime(reservation.StartDateTime),
        };
    }

    internal static string FormatReservationReference(Reservation reservation)
    {
        var names = reservation.Items
            .Select(item => item.RentalAsset?.Asset?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return names.Count > 0 ? string.Join(" + ", names) : "reserva";
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

        foreach (var eventType in RentalEventTypes.Notifying)
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
}

internal static class RentalsNotificationTemplates
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
        new(RentalEventTypes.ReservationPendingDeposit, NotificationChannel.InApp, "Reserva {{reservationReference}} aguardando depósito."),
        new(RentalEventTypes.ReservationPendingDeposit, NotificationChannel.Email, "Olá {{customerName}}, sua reserva {{reservationReference}} está aguardando depósito.", "Reserva {{reservationReference}}"),
        new(RentalEventTypes.ReservationPendingDeposit, NotificationChannel.WhatsApp, "Reserva {{reservationReference}} aguardando depósito.", WhatsAppTemplateName: "rental_reservation_status_update"),
        new(RentalEventTypes.ReservationConfirmed, NotificationChannel.InApp, "Reserva {{reservationReference}} confirmada."),
        new(RentalEventTypes.ReservationConfirmed, NotificationChannel.Email, "Olá {{customerName}}, sua reserva {{reservationReference}} foi confirmada.", "Reserva {{reservationReference}} confirmada"),
        new(RentalEventTypes.ReservationConfirmed, NotificationChannel.WhatsApp, "Reserva {{reservationReference}} confirmada.", WhatsAppTemplateName: "rental_reservation_status_update"),
        new(RentalEventTypes.ReservationCanceled, NotificationChannel.InApp, "Reserva {{reservationReference}} cancelada."),
        new(RentalEventTypes.ReservationCanceled, NotificationChannel.Email, "Olá {{customerName}}, sua reserva {{reservationReference}} foi cancelada.", "Reserva {{reservationReference}} cancelada"),
        new(RentalEventTypes.ReservationCanceled, NotificationChannel.WhatsApp, "Reserva {{reservationReference}} cancelada.", WhatsAppTemplateName: "rental_reservation_status_update"),
        new(RentalEventTypes.ReservationCompleted, NotificationChannel.InApp, "Reserva {{reservationReference}} concluída."),
        new(RentalEventTypes.ReservationCompleted, NotificationChannel.Email, "Olá {{customerName}}, sua reserva {{reservationReference}} foi concluída.", "Reserva {{reservationReference}} concluída"),
        new(RentalEventTypes.ReservationCompleted, NotificationChannel.WhatsApp, "Reserva {{reservationReference}} concluída.", WhatsAppTemplateName: "rental_reservation_status_update"),
        new(RentalEventTypes.ReservationReminder, NotificationChannel.InApp, "Lembrete: reserva {{reservationReference}} em {{reservationDateTime}}."),
        new(RentalEventTypes.ReservationReminder, NotificationChannel.Email, "Olá {{customerName}}, lembrete da reserva {{reservationReference}} em {{reservationDateTime}}.", "Lembrete de reserva {{reservationReference}}"),
        new(RentalEventTypes.ReservationReminder, NotificationChannel.WhatsApp, "Lembrete: reserva {{reservationReference}} em {{reservationDateTime}}.", WhatsAppTemplateName: "rental_reservation_reminder"),
    ];
}
