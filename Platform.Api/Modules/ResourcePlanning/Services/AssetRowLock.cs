using Microsoft.EntityFrameworkCore;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.ResourcePlanning.Services;

/// <summary>
/// <c>SELECT ... FOR UPDATE</c> on the <c>assets.assets</c> row: the one serialization point of
/// resource planning writes on an asset (allocations, usage transitions and the planning profile
/// with its working hours). Every statement carries the owning tenant id, so a foreign id never
/// locks or waits on another tenant's row. Rows are locked in ascending id order. A no-op on a
/// non-relational provider, and it must run inside the caller's transaction to hold.
/// </summary>
internal static class AssetRowLock
{
    public static async Task LockAsync(
        AppDbContext db,
        Guid tenantId,
        IEnumerable<Guid> assetIds,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id is required to lock assets.");
        }

        var ordered = assetIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

        if (ordered.Count == 0 || !IsPostgres(db))
        {
            return;
        }

        foreach (var assetId in ordered)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                SELECT id
                FROM assets.assets
                WHERE id = {assetId}
                  AND tenant_id = {tenantId}
                FOR UPDATE
                """,
                cancellationToken);
        }
    }

    private static bool IsPostgres(AppDbContext db) =>
        db.Database.IsRelational()
        && db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;
}
