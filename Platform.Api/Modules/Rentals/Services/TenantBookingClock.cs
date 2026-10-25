using Platform.Api.Services.Tenancy;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>Booking operations use the platform tenant time zone and civil-time conversions.</summary>
internal static class TenantBookingClock
{
    public static Task<TimeZoneInfo> ResolveForTenantAsync(
        AppDbContext db, Guid tenantId, CancellationToken cancellationToken) =>
        TenantTimeZoneProvider.ResolveForTenantAsync(db, tenantId, cancellationToken);

    public static Task<string> ResolveIanaIdForTenantAsync(
        AppDbContext db, Guid tenantId, CancellationToken cancellationToken) =>
        TenantTimeZoneProvider.ResolveIanaIdForTenantAsync(db, tenantId, cancellationToken);

    public static Task<string?> ReadConfiguredTimeZoneIdAsync(
        AppDbContext db, Guid tenantId, CancellationToken cancellationToken) =>
        TenantTimeZoneProvider.ReadConfiguredTimeZoneIdAsync(db, tenantId, cancellationToken);

    public static TimeZoneInfo Resolve(string? configuredTimeZoneId) =>
        TenantTimeZoneProvider.Resolve(configuredTimeZoneId);

    public static (DateTimeOffset Start, DateTimeOffset EndExclusive) CivilDay(
        DateOnly date, TimeZoneInfo zone) =>
        TenantTimeZoneProvider.CivilDay(date, zone);

    public static readonly TimeOnly DayClose = new(23, 59, 59);

    public static bool IsDayClose(TimeOnly endTime) => endTime >= DayClose;

    public static TimeOnly NormalizeEndTime(TimeOnly endTime) =>
        IsDayClose(endTime) ? DayClose : endTime;

    public static TimeSpan EndOfDayTime(TimeOnly endTime) =>
        IsDayClose(endTime) ? TimeSpan.FromDays(1) : endTime.ToTimeSpan();

    // TimeOnly cannot represent 24:00; the final second denotes the exclusive civil-day end.
    public static DateTimeOffset EndAtLocal(DateOnly date, TimeOnly endTime, TimeZoneInfo zone) =>
        IsDayClose(endTime) ? CivilDay(date, zone).EndExclusive : AtLocal(date, endTime, zone);

    public static DateTimeOffset AtLocal(DateOnly date, TimeOnly time, TimeZoneInfo zone) =>
        TenantTimeZoneProvider.AtLocal(date, time, zone);
}
