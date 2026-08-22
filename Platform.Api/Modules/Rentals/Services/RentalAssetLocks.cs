using Microsoft.EntityFrameworkCore;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

internal static class RentalAssetLocks
{
    public static Task LockByRentalAssetIdAsync(
        AppDbContext db,
        Guid rentalAssetId,
        CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            return Task.CompletedTask;
        }

        return db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM rentals.rental_assets WHERE id = {rentalAssetId} FOR UPDATE",
            ct);
    }
}
