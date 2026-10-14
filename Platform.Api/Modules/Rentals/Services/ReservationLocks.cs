using Microsoft.EntityFrameworkCore;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

internal static class ReservationLocks
{
    public static Task LockByReservationIdAsync(
        AppDbContext db,
        Guid tenantId,
        Guid reservationId,
        CancellationToken ct)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id is required to lock reservations.");
        }

        if (!db.Database.IsRelational())
        {
            return Task.CompletedTask;
        }

        return db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            SELECT 1
            FROM rentals.reservations
            WHERE id = {reservationId}
              AND tenant_id = {tenantId}
            FOR UPDATE
            """,
            ct);
    }
}
