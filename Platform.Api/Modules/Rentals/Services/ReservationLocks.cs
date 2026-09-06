using Microsoft.EntityFrameworkCore;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

internal static class ReservationLocks
{
    public static Task LockByReservationIdAsync(
        AppDbContext db,
        Guid reservationId,
        CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            return Task.CompletedTask;
        }

        return db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM rentals.reservations WHERE id = {reservationId} FOR UPDATE",
            ct);
    }
}
