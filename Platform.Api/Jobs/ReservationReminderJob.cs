using Hangfire;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Notifications;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Jobs;

public sealed class ReservationReminderJob(
    AppDbContext dbContext,
    IRentalsNotificationPublisher notificationPublisher,
    INotificationOutboxScheduler outboxScheduler,
    AmbientTenantContext ambientTenantContext,
    TimeProvider timeProvider,
    ILogger<ReservationReminderJob> logger)
{
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var windowEnd = now.Add(ReservationReminderSchedule.LeadTime);

        logger.LogInformation(
            "Scanning reservations for WhatsApp reminders due before {WindowEnd}.",
            windowEnd);

        var reminderTenantIds = await dbContext.TenantNotificationChannelConfigs
            .AsNoTracking()
            .Where(c =>
                c.EventType == RentalEventTypes.ReservationReminder
                && c.Channel == NotificationChannel.WhatsApp
                && c.IsActive)
            .Select(c => c.TenantId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var published = 0;

        foreach (var tenantId in reminderTenantIds)
        {
            ambientTenantContext.TenantId = tenantId;
            try
            {
                await notificationPublisher.EnsureReadyAsync(cancellationToken);
                published += await PublishDueForCurrentTenantAsync(now, windowEnd, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to publish reservation reminders for tenant {TenantId}.",
                    tenantId);
            }
        }

        ambientTenantContext.TenantId = null;

        logger.LogInformation(
            "Reservation reminder scan finished. Published {Published} reminder(s) across {TenantCount} tenant(s).",
            published,
            reminderTenantIds.Count);
    }

    private async Task<int> PublishDueForCurrentTenantAsync(
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
