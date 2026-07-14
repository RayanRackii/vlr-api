using Microsoft.EntityFrameworkCore;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Jobs;

public sealed class DataRetentionJob(
    AppDbContext dbContext,
    ILogger<DataRetentionJob> logger)
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        logger.LogInformation(
            "DataRetentionJob started at {UtcNow}.",
            now);

        var expiredCategoryIds = await dbContext.AssetCategories
            .Where(category =>
                category.ScheduledDeletionAt != null
                && category.ScheduledDeletionAt <= now)
            .Select(category => category.Id)
            .ToListAsync(cancellationToken);

        var blockedCategoryIds = await dbContext.MaintenancePlans
            .Where(plan => expiredCategoryIds.Contains(plan.AssetCategoryId))
            .Select(plan => plan.AssetCategoryId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (blockedCategoryIds.Count > 0)
        {
            logger.LogWarning(
                "Skipping hard delete for {Count} expired asset categor(ies) still referenced by maintenance plans.",
                blockedCategoryIds.Count);
        }

        var expiredCategories = await dbContext.AssetCategories
            .Where(category =>
                expiredCategoryIds.Contains(category.Id)
                && !blockedCategoryIds.Contains(category.Id))
            .ToListAsync(cancellationToken);

        if (expiredCategories.Count > 0)
        {
            dbContext.AssetCategories.RemoveRange(expiredCategories);
            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Hard-deleted {Count} expired asset categor(ies).",
                expiredCategories.Count);
        }

        var expiredAssets = await dbContext.Assets
            .Where(asset =>
                asset.ScheduledDeletionAt != null
                && asset.ScheduledDeletionAt <= now)
            .ToListAsync(cancellationToken);

        if (expiredAssets.Count > 0)
        {
            dbContext.Assets.RemoveRange(expiredAssets);
            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Hard-deleted {Count} expired asset(s).",
                expiredAssets.Count);
        }

        logger.LogInformation("DataRetentionJob completed.");
    }
}
