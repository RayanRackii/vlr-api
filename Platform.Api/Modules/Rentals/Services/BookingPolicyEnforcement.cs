using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>
/// Tenant default booking policy. Applies to every bookable resource type.
/// A row with a non-null resource type is ignored until a per-type override exists.
/// Enforced only on customer self-booking (<c>CreateReservation</c> and <c>BookSlot</c>).
/// There is no staff create-reservation API; confirm, cancel, and complete are not capped.
/// Active means <see cref="ReservationStatus.PendingDeposit"/> or
/// <see cref="ReservationStatus.Confirmed"/>. <see cref="ReservationStatus.Completed"/>
/// and <see cref="ReservationStatus.Canceled"/> do not count. The customer row lock
/// still wraps the recount, so dropping Completed does not change the race.
/// The civil day uses the tenant timezone when <see cref="Platform.Core.Domain.Entities.Tenant"/> has one.
/// It has none, so the window is America/Sao_Paulo.
/// </summary>
internal static class BookingPolicyEnforcement
{
    public static async Task EnsureCustomerCanBookAsync(
        AppDbContext db,
        Guid tenantId,
        Guid customerId,
        DateOnly civilDate,
        CancellationToken cancellationToken)
    {
        var limit = await ReadDailyLimitAsync(db, tenantId, cancellationToken);
        if (limit is not int)
        {
            return;
        }

        await CustomerLocks.LockAsync(db, tenantId, customerId, cancellationToken);

        limit = await ReadDailyLimitAsync(db, tenantId, cancellationToken);
        if (limit is not int max)
        {
            return;
        }

        var zone = await TenantBookingClock.ResolveForTenantAsync(db, tenantId, cancellationToken);
        var (dayStart, dayEnd) = TenantBookingClock.CivilDay(civilDate, zone);
        var activeCount = await db.Reservations
            .Where(reservation =>
                reservation.TenantId == tenantId
                && reservation.CustomerId == customerId
                && (reservation.Status == ReservationStatus.PendingDeposit
                    || reservation.Status == ReservationStatus.Confirmed)
                && reservation.StartDateTime >= dayStart
                && reservation.StartDateTime < dayEnd)
            .CountAsync(cancellationToken);

        if (activeCount >= max)
        {
            throw new DailyBookingLimitReachedException(max);
        }
    }

    private static async Task<int?> ReadDailyLimitAsync(
        AppDbContext db,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var policy = await db.BookingPolicies
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.ResourceType == null)
            .Select(item => new { item.MaxActiveReservationsPerCustomerPerDay })
            .FirstOrDefaultAsync(cancellationToken);

        return policy?.MaxActiveReservationsPerCustomerPerDay;
    }
}
