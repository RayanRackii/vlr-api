using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Assets;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Jobs;

public sealed class DataRetentionJob(
    AppDbContext dbContext,
    ILogger<DataRetentionJob> logger)
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var _ = PlatformScope.Begin(logger, "data-retention-cleanup");
        var now = DateTimeOffset.UtcNow;
        var brazilToday = HangfireExtensions.GetBrazilToday();

        logger.LogInformation(
            "Iniciando limpeza de retenção de dados para {BrazilDate} (UTC {UtcNow}).",
            brazilToday,
            now);

        try
        {
            if (dbContext.Database.IsNpgsql())
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    // Skip login-held rows so cleanup cannot invert the two-bucket lock order.
                    """
                    WITH expired AS (
                        SELECT bucket_key FROM core.customer_login_throttle
                        WHERE expires_at <= statement_timestamp()
                        ORDER BY bucket_key FOR UPDATE SKIP LOCKED
                    )
                    DELETE FROM core.customer_login_throttle AS bucket
                    USING expired WHERE bucket.bucket_key = expired.bucket_key
                    """,
                    cancellationToken);
            }

            await DeleteExpiredIdempotencyKeysAsync(now, cancellationToken);

            var expiredCategoryIds = await dbContext.AssetCategories
                .Where(category =>
                    category.ScheduledDeletionAt != null
                    && category.ScheduledDeletionAt <= now)
                .Select(category => category.Id)
                .ToListAsync(cancellationToken);

            var blockedByPlans = await dbContext.MaintenancePlans
                .Where(plan => plan.AssetCategoryId != null && expiredCategoryIds.Contains(plan.AssetCategoryId.Value))
                .Select(plan => plan.AssetCategoryId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken);

            var blockedByWorkOrders = await dbContext.Assets
                .Where(asset => expiredCategoryIds.Contains(asset.CategoryId))
                .Where(asset => dbContext.WorkOrders.Any(workOrder => workOrder.AssetId == asset.Id))
                .Select(asset => asset.CategoryId)
                .Distinct()
                .ToListAsync(cancellationToken);

            var blockedCategoryIds = blockedByPlans
                .Concat(blockedByWorkOrders)
                .Distinct()
                .ToList();

            if (blockedCategoryIds.Count > 0)
            {
                logger.LogWarning(
                    "Skipping hard delete for {Count} expired asset categor(ies) still referenced by maintenance plans or work orders.",
                    blockedCategoryIds.Count);
            }

            var expiredCategories = await dbContext.AssetCategories
                .Where(category =>
                    expiredCategoryIds.Contains(category.Id)
                    && !blockedCategoryIds.Contains(category.Id))
                .ToListAsync(cancellationToken);

            var deletedCategories = 0;
            foreach (var category in expiredCategories)
            {
                if (await TryHardDeleteAsync(
                        () =>
                        {
                            dbContext.AssetCategories.Remove(category);
                        },
                        "asset category",
                        category.Id,
                        cancellationToken))
                {
                    deletedCategories++;
                }
            }

            if (deletedCategories > 0)
            {
                logger.LogInformation(
                    "Hard-deleted {Count} expired asset categor(ies).",
                    deletedCategories);
            }

            var assetsWithWorkOrders = dbContext.WorkOrders.Select(workOrder => workOrder.AssetId);
            var expiredAssets = await dbContext.Assets
                .Where(asset =>
                    asset.ScheduledDeletionAt != null
                    && asset.ScheduledDeletionAt <= now
                    && !assetsWithWorkOrders.Contains(asset.Id))
                .ToListAsync(cancellationToken);

            var skippedAssets = await dbContext.Assets
                .CountAsync(
                    asset =>
                        asset.ScheduledDeletionAt != null
                        && asset.ScheduledDeletionAt <= now
                        && assetsWithWorkOrders.Contains(asset.Id),
                    cancellationToken);

            if (skippedAssets > 0)
            {
                logger.LogWarning(
                    "Skipping hard delete for {Count} expired asset(s) still referenced by work orders.",
                    skippedAssets);
            }

            var deletedAssets = 0;
            foreach (var asset in expiredAssets)
            {
                if (await TryHardDeleteAsync(
                        () =>
                        {
                            dbContext.Assets.Remove(asset);
                        },
                        "asset",
                        asset.Id,
                        cancellationToken))
                {
                    deletedAssets++;
                }
            }

            if (deletedAssets > 0)
            {
                logger.LogInformation(
                    "Hard-deleted {Count} expired asset(s).",
                    deletedAssets);
            }

            logger.LogInformation("DataRetentionJob completed successfully.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DataRetentionJob failed.");
            throw;
        }
    }

    /// <summary>
    /// Idempotency keys live 24 hours (<see cref="IdempotencyKey.Retention"/>). The rows are
    /// transient replay bookkeeping, deleted across all tenants. Like the other sweeps here, this runs inside the
    /// job's <see cref="PlatformScope"/>, which lifts the tenant filter, so no IgnoreQueryFilters is needed.
    /// </summary>
    private async Task DeleteExpiredIdempotencyKeysAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var expired = await dbContext.IdempotencyKeys
            .Where(key => key.ExpiresAt <= now)
            .ToListAsync(cancellationToken);
        if (expired.Count == 0)
        {
            return;
        }

        dbContext.IdempotencyKeys.RemoveRange(expired);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Deleted {Count} expired idempotency key(s).", expired.Count);
    }

    private async Task<bool> TryHardDeleteAsync(
        Action markDeleted,
        string entityName,
        Guid entityId,
        CancellationToken cancellationToken)
    {
        markDeleted();
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (AssetPersistenceConflicts.IsForeignKeyViolation(ex))
        {
            logger.LogWarning(
                "Skipping hard delete of {EntityName} {EntityId} because a foreign key still references it.",
                entityName,
                entityId);

            foreach (var entry in dbContext.ChangeTracker.Entries().ToList())
            {
                if (entry.State == EntityState.Deleted)
                {
                    entry.State = EntityState.Unchanged;
                }
            }

            return false;
        }
    }
}
