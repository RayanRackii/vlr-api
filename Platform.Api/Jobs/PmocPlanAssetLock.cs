using Microsoft.EntityFrameworkCore;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Jobs;

/// <summary>
/// Serializes PMOC generation with edits to an existing plan-linked work order.
/// Existing-order writes (<c>LockAndLoadAsync</c>) lock that work-order row, then
/// take the plan/asset advisory lock. Create-with-plan and manual generation take
/// the advisory first and insert a new order. Automatic generation takes the
/// advisory, then the plan row, then the asset row, and inserts a new order.
/// Plan header updates lock only the plan row.
/// Those orders do not cycle: a creator never waits on an existing work-order row,
/// and an existing-order write never locks the plan or asset row.
/// Plan and asset <c>FOR UPDATE</c> statements also require the owning tenant id.
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
        Guid tenantId,
        Guid planId,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id is required to lock maintenance plans.");
        }

        if (!IsPostgres(db))
        {
            return Task.CompletedTask;
        }

        return db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            SELECT id
            FROM pmoc.maintenance_plans
            WHERE id = {planId}
              AND tenant_id = {tenantId}
            FOR UPDATE
            """,
            cancellationToken);
    }

    public static Task LockAssetRowAsync(
        AppDbContext db,
        Guid tenantId,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id is required to lock assets.");
        }

        if (!IsPostgres(db))
        {
            return Task.CompletedTask;
        }

        return db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            SELECT id
            FROM assets.assets
            WHERE id = {assetId}
              AND tenant_id = {tenantId}
            FOR UPDATE
            """,
            cancellationToken);
    }

    private static bool IsPostgres(AppDbContext db) =>
        db.Database.IsRelational()
        && db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;
}
