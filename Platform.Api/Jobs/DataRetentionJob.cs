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
        var brazilToday = HangfireExtensions.GetBrazilToday();

        logger.LogInformation(
            "Iniciando limpeza de retenção de dados para {BrazilDate} (UTC {UtcNow}).",
            brazilToday,
            now);

        try
        {
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

            logger.LogInformation("DataRetentionJob completed successfully.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DataRetentionJob failed.");
            throw;
        }
    }
}
