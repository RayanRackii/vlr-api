using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using Platform.Api.DomainEvents;
using Platform.Api.Notifications;
using Platform.Core.Domain.Events;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using System.Globalization;
using NotificationEntity = Platform.Core.Domain.Entities.Notification;

namespace Platform.Api.Modules.Rentals.Services;

public interface IRentalsNotificationPublisher : IDomainEventHandler<ReservationEvent>
{
    Task<IReadOnlyList<Guid>> IDomainEventHandler<ReservationEvent>.HandleAsync(
        ReservationEvent domainEvent, CancellationToken cancellationToken) =>
        PublishReservationEventAsync(domainEvent.Reservation, domainEvent.EventType, cancellationToken);

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
    ITenantProvider tenantProvider,
    ILogger<RentalsNotificationPublisher> logger,
    IConfiguration? configuration = null,
    IHostEnvironment? environment = null) : IRentalsNotificationPublisher
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
        if (tenantProvider.TenantId is not Guid tenantId)
        {
            logger.LogWarning(
                "Rentals reservation notification skipped because the ambient tenant is missing. EventType={EventType}",
                eventType);
            return [];
        }

        if (reservation.TenantId != tenantId)
        {
            logger.LogWarning(
                "Rentals reservation notification skipped because the reservation tenant does not match the ambient tenant. EventType={EventType}",
                eventType);
            return [];
        }

        if (!RentalEventTypes.Notifying.Contains(eventType))
        {
            return [];
        }

        if (eventType == RentalEventTypes.ReservationReminder)
        {
            var alreadyReminded = await dbContext.Notifications.AnyAsync(
                n => n.TenantId == tenantId
                     && n.EventType == RentalEventTypes.ReservationReminder
                     && n.AggregateType == "Reservation"
                     && n.AggregateId == reservation.Id,
                cancellationToken);
            if (alreadyReminded)
            {
                return [];
            }
        }

        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

        var payload = await BuildPayloadAsync(
            reservation,
            tenant,
            tenantId,
            cancellationToken);

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

        var recipientEmail = await ResolveRecipientEmailAsync(
            reservation,
            tenantId,
            cancellationToken);

        var notification = new NotificationEntity
        {
            TenantId = tenantId,
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
                TenantId = tenantId,
                NotificationId = notification.Id,
                Channel = config.Channel,
                RecipientKind = NotificationRecipientKind.Customer,
                RecipientId = reservation.CustomerId,
                RecipientName = reservation.CustomerName,
                RecipientEmail = recipientEmail,
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

    private async Task<string?> ResolveRecipientEmailAsync(
        Reservation reservation,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(reservation.Customer?.Email))
        {
            return reservation.Customer.Email;
        }

        return await dbContext.Customers
            .AsNoTracking()
            .Where(customer => customer.TenantId == tenantId && customer.Id == reservation.CustomerId)
            .Select(customer => customer.Email)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<Dictionary<string, string?>> BuildPayloadAsync(
        Reservation reservation,
        Tenant? tenant,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var tenantName = tenant?.TradeName is { Length: > 0 } trade
            ? trade
            : tenant?.LegalName;
        var reference = FormatReservationReference(reservation);
        var zone = await TenantBookingClock.ResolveForTenantAsync(dbContext, tenantId, cancellationToken);
        var schedule = ReservationCustomerEmail.FormatSchedule(
            reservation.StartDateTime,
            reservation.EndDateTime,
            zone);
        var portalUrl = configuration is null || environment is null
            ? null
            : CustomerPortalLinks.ReservationAgenda(
                FrontendBaseUrlResolver.Resolve(configuration, environment),
                tenant?.Subdomain,
                reservation.Id);

        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ReservationCustomerEmail.TenantNameKey] = tenantName,
            [ReservationCustomerEmail.CustomerNameKey] = reservation.CustomerName,
            [ReservationCustomerEmail.ReferenceKey] = reference,
            [ReservationCustomerEmail.StatusKey] = NotificationCustomerCopy.Label(reservation.Status),
            [ReservationCustomerEmail.DateTimeKey] = TimeZoneInfo.ConvertTime(reservation.StartDateTime, zone)
                .ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR")),
            [ReservationCustomerEmail.DateKey] = schedule.Date,
            [ReservationCustomerEmail.StartKey] = schedule.Start,
            [ReservationCustomerEmail.EndKey] = schedule.End,
            [ReservationCustomerEmail.TotalKey] = ReservationCustomerEmail.FormatMoney(reservation.TotalAmount),
            [ReservationCustomerEmail.DepositKey] = await ResolveDepositTextAsync(
                reservation,
                tenantId,
                zone,
                cancellationToken),
            [ReservationCustomerEmail.UrlKey] = portalUrl,
        };
    }

    private async Task<string?> ResolveDepositTextAsync(
        Reservation reservation,
        Guid tenantId,
        TimeZoneInfo zone,
        CancellationToken cancellationToken)
    {
        if (reservation.Status != ReservationStatus.PendingDeposit || reservation.TotalAmount <= 0m)
        {
            return null;
        }

        var startLocal = TimeZoneInfo.ConvertTime(reservation.StartDateTime, zone);
        var endLocal = TimeZoneInfo.ConvertTime(reservation.EndDateTime, zone);
        if (startLocal.Date != endLocal.Date)
        {
            return null;
        }

        var date = DateOnly.FromDateTime(startLocal.Date);
        var startTime = TimeOnly.FromTimeSpan(startLocal.TimeOfDay);
        var endTime = TimeOnly.FromTimeSpan(endLocal.TimeOfDay);
        decimal deposit = 0m;
        var counted = 0;

        foreach (var item in reservation.Items)
        {
            if (item.TenantId != tenantId)
            {
                continue;
            }

            var requiresDeposit = item.RentalAsset?.RequiresDeposit;
            if (item.RentalAsset is null)
            {
                requiresDeposit = await dbContext.RentalAssets
                    .AsNoTracking()
                    .Where(rental => rental.TenantId == tenantId && rental.Id == item.RentalAssetId)
                    .Select(rental => (bool?)rental.RequiresDeposit)
                    .FirstOrDefaultAsync(cancellationToken);
            }

            if (requiresDeposit != true)
            {
                continue;
            }

            var percentage = await dbContext.RentalPricings
                .AsNoTracking()
                .Where(pricing => pricing.TenantId == tenantId && pricing.RentalAssetId == item.RentalAssetId)
                .Where(pricing => pricing.DayOfWeek == date.DayOfWeek)
                .Where(pricing => pricing.StartTime <= startTime && pricing.EndTime >= endTime)
                .OrderBy(pricing => pricing.PricePerHour)
                .Select(pricing => (decimal?)pricing.DepositPercentage)
                .FirstOrDefaultAsync(cancellationToken);

            if (percentage is null)
            {
                return null;
            }

            deposit += decimal.Round(
                item.SubTotal * percentage.Value / 100m,
                2,
                MidpointRounding.AwayFromZero);
            counted++;
        }

        return counted == 0 ? null : ReservationCustomerEmail.FormatMoney(deposit);
    }

    internal static string FormatReservationReference(Reservation reservation)
    {
        var names = reservation.Items
            .Where(item => item.TenantId == reservation.TenantId)
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
                    IsActive = channel is NotificationChannel.InApp or NotificationChannel.Email,
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

    // Email subject/body seeds are not the customer message. The outbox renders
    // ReservationCustomerEmail for these events so missing price or deposit lines drop out.
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
