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

    public static DateTimeOffset AtLocal(DateOnly date, TimeOnly time, TimeZoneInfo zone) =>
        TenantTimeZoneProvider.AtLocal(date, time, zone);
}
