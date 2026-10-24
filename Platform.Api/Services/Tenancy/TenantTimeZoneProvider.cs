using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Services.Tenancy;

/// <summary>
/// Tenant time zone and civil-time conversions: the IANA zone in the
/// <see cref="TenantSettingKeys.BookingTimeZoneId"/> tenant setting. A missing
/// row falls back to America/Sao_Paulo; an unknown id logs a warning and falls
/// back too, so reads never fail on a bad setting.
/// </summary>
internal static class TenantTimeZoneProvider
{
    public static async Task<TimeZoneInfo> ResolveForTenantAsync(
        AppDbContext db,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var configured = await ReadConfiguredTimeZoneIdAsync(db, tenantId, cancellationToken);
        var zone = Find(configured);
        if (zone is null && configured is not null)
        {
            db.GetService<ILoggerFactory>()
                .CreateLogger(typeof(TenantTimeZoneProvider))
                .LogWarning(
                    "Tenant {TenantId} has invalid {SettingKey} '{TimeZoneId}'; using {FallbackTimeZoneId}.",
                    tenantId,
                    TenantSettingKeys.BookingTimeZoneId,
                    configured,
                    BrazilTimeZone.Resolve().Id);
        }

        return zone ?? BrazilTimeZone.Resolve();
    }

    /// <summary>Browser-compatible effective IANA id, independent of the host OS.</summary>
    public static async Task<string> ResolveIanaIdForTenantAsync(
        AppDbContext db, Guid tenantId, CancellationToken cancellationToken)
    {
        var zone = await ResolveForTenantAsync(db, tenantId, cancellationToken);
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var ianaId)
            ? ianaId
            : zone.Id;
    }

    /// <summary>The tenant's raw configured zone id, or null when the setting is absent.</summary>
    public static Task<string?> ReadConfiguredTimeZoneIdAsync(
        AppDbContext db,
        Guid tenantId,
        CancellationToken cancellationToken) =>
        // The caller names the tenant; the ambient filter could be a different tenant (jobs, admin).
        db.TenantSettings
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(setting => setting.TenantId == tenantId
                && setting.Key == TenantSettingKeys.BookingTimeZoneId)
            .Select(setting => (string?)setting.Value)
            .FirstOrDefaultAsync(cancellationToken);

    public static TimeZoneInfo Resolve(string? configuredTimeZoneId) =>
        Find(configuredTimeZoneId) ?? BrazilTimeZone.Resolve();

    private static TimeZoneInfo? Find(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
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
    /// <see cref="BrazilTimeZone.AtLocal"/> so all consumers use the same instant.
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
