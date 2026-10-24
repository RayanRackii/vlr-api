using Hangfire;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Notifications;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Jobs;

public sealed class ReservationReminderJob(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ReservationReminderJob> logger)
{
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var windowEnd = now.Add(ReservationReminderSchedule.LeadTime);

        logger.LogInformation(
            "Scanning reservations for reminders due before {WindowEnd}.",
            windowEnd);

        var reminderTenantIds = await ListReminderTenantIdsAsync(cancellationToken);
        var published = 0;

        foreach (var tenantId in reminderTenantIds)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var ambientTenantContext = scope.ServiceProvider.GetRequiredService<AmbientTenantContext>();
            ambientTenantContext.TenantId = tenantId;
            try
            {
                var notificationPublisher = scope.ServiceProvider
                    .GetRequiredService<IRentalsNotificationPublisher>();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var outboxScheduler = scope.ServiceProvider.GetRequiredService<INotificationOutboxScheduler>();

                await notificationPublisher.EnsureReadyAsync(cancellationToken);
                published += await PublishDueForTenantAsync(
                    dbContext,
                    notificationPublisher,
                    outboxScheduler,
                    now,
                    windowEnd,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to publish reservation reminders for tenant {TenantId}.",
                    tenantId);
            }
            finally
            {
                ambientTenantContext.TenantId = null;
            }
        }

        logger.LogInformation(
            "Reservation reminder scan finished. Published {Published} reminder(s) across {TenantCount} tenant(s).",
            published,
            reminderTenantIds.Count);
    }

    private async Task<List<Guid>> ListReminderTenantIdsAsync(CancellationToken cancellationToken)
    {
        // Only the tenant listing is platform; per-tenant work runs outside this scope.
        using var _ = PlatformScope.Begin(logger, "reservation-reminder-tenant-sweep");
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Ambient tenant stays unset on this scope, so the global filter does not hide
        // other tenants. The query still returns only tenant ids.
        var activeRentalsTenantIds = dbContext.TenantModules
            .AsNoTracking()
            .Where(module => module.ModuleName == PlatformModules.Rentals && module.IsActive)
            .Select(module => module.TenantId);

        return await dbContext.TenantNotificationChannelConfigs
            .AsNoTracking()
            .Where(c =>
                c.EventType == RentalEventTypes.ReservationReminder
                && c.IsActive
                && (c.Channel == NotificationChannel.Email
                    || c.Channel == NotificationChannel.WhatsApp)
                && activeRentalsTenantIds.Contains(c.TenantId))
            .Select(c => c.TenantId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    private async Task<int> PublishDueForTenantAsync(
        AppDbContext dbContext,
        IRentalsNotificationPublisher notificationPublisher,
        INotificationOutboxScheduler outboxScheduler,
        DateTimeOffset now,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        var alreadyReminded = dbContext.Notifications
            .Where(n =>
                n.EventType == RentalEventTypes.ReservationReminder
                && n.AggregateType == "Reservation")
            .Select(n => n.AggregateId);

        var dueIds = await dbContext.Reservations
            .Where(r =>
                (r.Status == ReservationStatus.PendingDeposit
                    || r.Status == ReservationStatus.Confirmed)
                && r.StartDateTime > now
                && r.StartDateTime <= windowEnd
                && !alreadyReminded.Contains(r.Id))
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

        var published = 0;

        foreach (var reservationId in dueIds)
        {
            try
            {
                var reservation = await dbContext.Reservations
                    .Include(r => r.Customer)
                    .Include(r => r.Items)
                        .ThenInclude(i => i.RentalAsset)
                            .ThenInclude(a => a.Asset)
                    .FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken);

                if (reservation is null)
                {
                    continue;
                }

                if (reservation.Status is ReservationStatus.Canceled or ReservationStatus.Completed)
                {
                    continue;
                }

                if (!ReservationReminderSchedule.IsInLeadWindow(reservation.StartDateTime, now))
                {
                    continue;
                }

                var queued = await notificationPublisher.PublishReservationEventAsync(
                    reservation,
                    RentalEventTypes.ReservationReminder,
                    cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);

                foreach (var deliveryId in queued)
                {
                    outboxScheduler.Schedule(deliveryId);
                }

                if (queued.Count > 0)
                {
                    published++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to publish reminder for reservation {ReservationId}.",
                    reservationId);
            }
        }

        return published;
    }
}
