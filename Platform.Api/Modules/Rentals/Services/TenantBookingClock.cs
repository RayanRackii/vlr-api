using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>
/// Civil day for the tenant booking-policy cap.
/// <see cref="Tenant"/> has no timezone property. A missing, blank, or unknown
/// id falls back to America/Sao_Paulo, the same clock that stores reservation
/// instants, so the cap window matches <see cref="Reservation.StartDateTime"/>.
/// </summary>
internal static class TenantBookingClock
{
    public static async Task<TimeZoneInfo> ResolveForTenantAsync(
        AppDbContext db,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == tenantId, cancellationToken);
        return Resolve(tenant is null ? null : ReadConfiguredTimeZoneId(tenant));
    }

    /// <summary>
    /// Returns the tenant's configured timezone id when the model has one.
    /// <see cref="Tenant"/> currently has no such property, so this is null.
    /// </summary>
    public static string? ReadConfiguredTimeZoneId(Tenant tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return null;
    }

    public static TimeZoneInfo Resolve(string? configuredTimeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(configuredTimeZoneId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(configuredTimeZoneId.Trim());
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return BrazilTimeZone.Resolve();
    }

    public static (DateTimeOffset Start, DateTimeOffset EndExclusive) CivilDay(
        DateOnly date,
        TimeZoneInfo zone)
    {
        var fallback = BrazilTimeZone.Resolve();
        if (string.Equals(zone.Id, fallback.Id, StringComparison.Ordinal))
        {
            return (BrazilTimeZone.StartOfCivilDay(date), BrazilTimeZone.ExclusiveEndOfCivilDay(date));
        }

        return (
            AtLocal(date, TimeOnly.MinValue, zone),
            AtLocal(date.AddDays(1), TimeOnly.MinValue, zone));
    }

    /// <summary>
    /// Civil <paramref name="date"/> and <paramref name="time"/> in <paramref name="zone"/>
    /// as a UTC instant. The platform fallback zone uses
    /// <see cref="BrazilTimeZone.AtLocal"/> so reservation storage and the past-start
    /// check stay the same instant.
    /// </summary>
    public static DateTimeOffset AtLocal(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var fallback = BrazilTimeZone.Resolve();
        if (string.Equals(zone.Id, fallback.Id, StringComparison.Ordinal))
        {
            return BrazilTimeZone.AtLocal(date, time);
        }

        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }
}
