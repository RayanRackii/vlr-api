using Microsoft.EntityFrameworkCore;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

internal static class RentalAssetLocks
{
    public static Task LockByRentalAssetIdAsync(
        AppDbContext db,
        Guid tenantId,
        Guid rentalAssetId,
        CancellationToken ct) =>
        LockByRentalAssetIdsAsync(db, tenantId, [rentalAssetId], ct);

    public static async Task LockByRentalAssetIdsAsync(
        AppDbContext db,
        Guid tenantId,
        IEnumerable<Guid> rentalAssetIds,
        CancellationToken ct)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id is required to lock rental assets.");
        }

        var ordered = rentalAssetIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

        if (ordered.Count == 0 || !db.Database.IsRelational())
        {
            return;
        }

        foreach (var rentalAssetId in ordered)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                SELECT 1
                FROM rentals.rental_assets
                WHERE id = {rentalAssetId}
                  AND tenant_id = {tenantId}
                FOR UPDATE
                """,
                ct);
        }
    }
}
