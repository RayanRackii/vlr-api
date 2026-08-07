using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Admin.Services;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Jobs;

public sealed class TrialLifecycleJob(
    AppDbContext dbContext,
    IAdminTenantService adminTenantService,
    ILogger<TrialLifecycleJob> logger)
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        logger.LogInformation(
            "Starting trial lifecycle purge for UTC {UtcNow}.",
            now);

        try
        {
            var dueTenantIds = await dbContext.Tenants
                .AsNoTracking()
                .Where(t =>
                    t.IsTrial
                    && t.IsActive
                    && t.TrialPurgeAt != null
                    && t.TrialPurgeAt <= now)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);

            if (dueTenantIds.Count == 0)
            {
                logger.LogInformation("No trial tenants due for purge.");
                return;
            }

            foreach (var tenantId in dueTenantIds)
            {
                try
                {
                    await adminTenantService.DeleteAsync(tenantId, cancellationToken);
                    logger.LogInformation("Purged trial tenant {TenantId}.", tenantId);
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Failed to purge trial tenant {TenantId}.",
                        tenantId);
                }
            }

            logger.LogInformation(
                "TrialLifecycleJob finished. Attempted purge of {Count} tenant(s).",
                dueTenantIds.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TrialLifecycleJob failed.");
            throw;
        }
    }
}
