using Microsoft.EntityFrameworkCore;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Jobs;

/// <summary>
/// Daily, tenant-agnostic cleanup of the in-app inbox. Deletes InApp deliveries that were read more
/// than 30 days ago, and any InApp delivery older than 90 days regardless of read state. Batched so a
/// large backlog never runs as one unbounded transaction. Runs as a system job (no tenant context),
/// consistent with the other Hangfire retention jobs.
/// </summary>
public sealed class NotificationRetentionJob(
    AppDbContext dbContext,
    ILogger<NotificationRetentionJob> logger,
    TimeProvider? timeProvider = null)
{
    internal static readonly TimeSpan ReadRetention = TimeSpan.FromDays(30);
    internal static readonly TimeSpan MaxAge = TimeSpan.FromDays(90);
    private const int BatchSize = 1000;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // A platform scope keeps the tenant query filter from hiding rows when there is no ambient
        // tenant (fail-closed deployments), so the sweep covers every tenant.
        using var _ = PlatformScope.Begin(logger, "notification-inapp-retention");

        var now = _timeProvider.GetUtcNow();
        var readCutoff = now - ReadRetention;
        var ageCutoff = now - MaxAge;

        logger.LogInformation(
            "Pruning in-app notifications read before {ReadCutoff} or created before {AgeCutoff}.",
            readCutoff,
            ageCutoff);

        var total = dbContext.Database.IsNpgsql()
            ? await DeleteBatchedNpgsqlAsync(readCutoff, ageCutoff, cancellationToken)
            : await DeleteFallbackAsync(readCutoff, ageCutoff, cancellationToken);

        logger.LogInformation("NotificationRetentionJob deleted {Count} in-app deliveries.", total);
    }

    private async Task<int> DeleteBatchedNpgsqlAsync(
        DateTimeOffset readCutoff,
        DateTimeOffset ageCutoff,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var deleted = await dbContext.Database.ExecuteSqlRawAsync(
                """
                DELETE FROM core.notification_deliveries AS target
                USING (
                    SELECT id FROM core.notification_deliveries
                    WHERE channel = 'InApp'
                      AND ((read_at IS NOT NULL AND read_at <= {0}) OR created_at <= {1})
                    ORDER BY id
                    LIMIT {2}
                    FOR UPDATE SKIP LOCKED
                ) AS doomed
                WHERE target.id = doomed.id
                """,
                [readCutoff, ageCutoff, BatchSize],
                cancellationToken);

            total += deleted;
            if (deleted < BatchSize)
            {
                break;
            }
        }

        return total;
    }

    private async Task<int> DeleteFallbackAsync(
        DateTimeOffset readCutoff,
        DateTimeOffset ageCutoff,
        CancellationToken cancellationToken)
    {
        var doomed = await dbContext.NotificationDeliveries
            .Where(d => d.Channel == Platform.Core.Domain.Enums.NotificationChannel.InApp
                && ((d.ReadAt != null && d.ReadAt <= readCutoff) || d.CreatedAt <= ageCutoff))
            .ToListAsync(cancellationToken);

        if (doomed.Count > 0)
        {
            dbContext.NotificationDeliveries.RemoveRange(doomed);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return doomed.Count;
    }
}
