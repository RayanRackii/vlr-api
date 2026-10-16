using Microsoft.EntityFrameworkCore;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>
/// How far ahead a booking may start. Days are civil dates in the zone from
/// <see cref="TenantBookingClock"/> (configured tenant zone, otherwise that
/// clock's fallback). A null effective horizon is unlimited.
/// </summary>
internal static class BookingHorizon
{
    public static int? Effective(int? resourceOverride, int? tenantDays) =>
        resourceOverride ?? tenantDays;

    public static DateOnly CivilToday(DateTimeOffset utcNow, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow.UtcDateTime, zone);
        return DateOnly.FromDateTime(local);
    }

    public static bool IsBeyond(DateOnly startDate, DateOnly today, int? maxDays)
    {
        if (maxDays is not int limit)
        {
            return false;
        }

        var daysAhead = startDate.DayNumber - today.DayNumber;
        return daysAhead > limit;
    }

    public static void Ensure(DateOnly startDate, DateOnly today, int? maxDays)
    {
        if (IsBeyond(startDate, today, maxDays))
        {
            throw new BookingHorizonExceededException();
        }
    }

    public static async Task<int?> ReadTenantDaysAsync(
        AppDbContext db,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        return await db.BookingPolicies
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.ResourceType == null)
            .Select(item => item.MaxAdvanceBookingDays)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public static async Task EnsureAssetsAsync(
        AppDbContext db,
        Guid tenantId,
        IReadOnlyCollection<Guid> assetIds,
        DateOnly startDate,
        DateOnly today,
        int? tenantDays,
        CancellationToken cancellationToken)
    {
        var overrides = await db.RentalAssets
            .AsNoTracking()
            .Where(item =>
                item.TenantId == tenantId
                && assetIds.Contains(item.AssetId)
                && item.IsActive)
            .Select(item => item.MaxAdvanceBookingDays)
            .ToListAsync(cancellationToken);

        foreach (var overrideDays in overrides)
        {
            Ensure(startDate, today, Effective(overrideDays, tenantDays));
        }
    }

    public static async Task EnsureRentalAssetAsync(
        AppDbContext db,
        Guid tenantId,
        Guid rentalAssetId,
        DateOnly startDate,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var zone = await TenantBookingClock.ResolveForTenantAsync(db, tenantId, cancellationToken);
        var today = CivilToday(clock.GetUtcNow(), zone);
        var tenantDays = await ReadTenantDaysAsync(db, tenantId, cancellationToken);
        var overrideDays = await db.RentalAssets
            .AsNoTracking()
            .Where(item => item.Id == rentalAssetId && item.TenantId == tenantId)
            .Select(item => item.MaxAdvanceBookingDays)
            .FirstOrDefaultAsync(cancellationToken);

        Ensure(startDate, today, Effective(overrideDays, tenantDays));
    }

    /// <summary>
    /// Resource ids whose start date is still inside the horizon.
    /// An empty <paramref name="requestedIds"/> means every active rentable.
    /// Throws when every active resource in scope is past its horizon.
    /// Requested ids that match no active rentable return an empty set
    /// (an empty day), never the raw ids.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> ScopeForPublicDayAsync(
        AppDbContext db,
        Guid tenantId,
        DateOnly date,
        IReadOnlyList<Guid> requestedIds,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var zone = await TenantBookingClock.ResolveForTenantAsync(db, tenantId, cancellationToken);
        var today = CivilToday(clock.GetUtcNow(), zone);
        var tenantDays = await ReadTenantDaysAsync(db, tenantId, cancellationToken);

        var query = db.RentalAssets
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.IsActive && item.Asset.IsRentable);

        if (requestedIds.Count > 0)
        {
            query = query.Where(item => requestedIds.Contains(item.Id));
        }

        var rows = await query
            .Select(item => new { item.Id, item.MaxAdvanceBookingDays })
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            if (requestedIds.Count > 0)
            {
                return [];
            }

            if (IsBeyond(date, today, tenantDays))
            {
                throw new BookingHorizonExceededException();
            }

            return requestedIds;
        }

        var allowed = rows
            .Where(item => !IsBeyond(date, today, Effective(item.MaxAdvanceBookingDays, tenantDays)))
            .Select(item => item.Id)
            .ToList();

        if (allowed.Count == 0)
        {
            throw new BookingHorizonExceededException();
        }

        if (requestedIds.Count == 0 && allowed.Count == rows.Count)
        {
            return requestedIds;
        }

        return allowed;
    }
}
