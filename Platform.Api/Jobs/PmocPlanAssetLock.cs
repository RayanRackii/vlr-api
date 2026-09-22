using Microsoft.EntityFrameworkCore;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Jobs;

/// <summary>
/// Serializes PMOC automatic generation with completion and plan scheduling edits.
/// Lock order, never reversed: advisory(plan, asset) → plan row → asset row → work order write.
/// Manual generation takes only the advisory lock. Plan header updates take only the plan row.
/// </summary>
public static class PmocPlanAssetLock
{
    public static Task AcquireAsync(
        AppDbContext db,
        Guid planId,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        if (!IsPostgres(db))
        {
            return Task.CompletedTask;
        }

        var key = $"{planId:N}:{assetId:N}";
        long seed = 0;
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, {seed}))",
            cancellationToken);
    }

    public static Task LockPlanRowAsync(
        AppDbContext db,
        Guid planId,
        CancellationToken cancellationToken)
    {
        if (!IsPostgres(db))
        {
            return Task.CompletedTask;
        }

        return db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM pmoc.maintenance_plans WHERE id = {planId} FOR UPDATE",
            cancellationToken);
    }

    public static Task LockAssetRowAsync(
        AppDbContext db,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        if (!IsPostgres(db))
        {
            return Task.CompletedTask;
        }

        return db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM assets.assets WHERE id = {assetId} FOR UPDATE",
            cancellationToken);
    }

    private static bool IsPostgres(AppDbContext db) =>
        db.Database.IsRelational()
        && db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;
}
