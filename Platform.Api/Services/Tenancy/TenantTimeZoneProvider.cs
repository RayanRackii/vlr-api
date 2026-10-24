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
            StartOfCivilDay(date, zone),
            StartOfCivilDay(date.AddDays(1), zone));
    }

    private static DateTimeOffset StartOfCivilDay(DateOnly date, TimeZoneInfo zone)
    {
        var midnight = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return ToUtc(FirstValidLocal(midnight, zone), zone);
    }

    // Day boundaries use the end of a gap, rather than shifting midnight by its duration.
    private static DateTime FirstValidLocal(DateTime local, TimeZoneInfo zone)
    {
        if (!zone.IsInvalidTime(local))
        {
            return local;
        }

        var valid = local;
        do
        {
            valid = valid.AddHours(1);
        }
        while (zone.IsInvalidTime(valid));

        var invalid = local.Ticks;
        var end = valid.Ticks;
        while (end - invalid > 1)
        {
            var middle = invalid + (end - invalid) / 2;
            if (zone.IsInvalidTime(new DateTime(middle, DateTimeKind.Unspecified)))
                invalid = middle;
            else
                end = middle;
        }

        return new DateTime(end, DateTimeKind.Unspecified);
    }

    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone) =>
        // The larger offset gives the earlier of the two instants in an overlap.
        zone.IsAmbiguousTime(local)
            ? new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).ToUniversalTime()
            : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);

    /// <summary>
    /// Civil <paramref name="date"/> and <paramref name="time"/> in <paramref name="zone"/>
    /// as a UTC instant. A nonexistent time shifts forward by the gap; an ambiguous
    /// time uses the earlier instant. The platform fallback zone uses
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
        if (zone.IsInvalidTime(local))
        {
            var before = local;
            do
            {
                before = before.AddHours(-1);
            }
            while (zone.IsInvalidTime(before));

            var after = FirstValidLocal(local, zone);
            local += zone.GetUtcOffset(after) - zone.GetUtcOffset(before);
        }

        return ToUtc(local, zone);
    }
}
