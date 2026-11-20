using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Services.Tenancy;

/// <summary>
/// The one place that reads <c>core.tenant_settings</c> for a tenant the caller names, across the
/// ambient tenant filter (anonymous public routes, jobs and platform-admin calls have no matching
/// ambient tenant). Callers always pass the tenant id explicitly.
/// </summary>
public static class TenantSettingStore
{
    /// <summary>The (tenant, key) setting row; unique by index, so at most one.</summary>
    public static IQueryable<TenantSetting> Query(AppDbContext db, Guid tenantId, string key) =>
        db.TenantSettings
            .IgnoreQueryFilters()
            .Where(setting => setting.TenantId == tenantId && setting.Key == key);

    /// <summary>The raw value, or null when the tenant has no row for the key.</summary>
    public static Task<string?> ReadValueAsync(
        AppDbContext db,
        Guid tenantId,
        string key,
        CancellationToken cancellationToken) =>
        Query(db, tenantId, key)
            .AsNoTracking()
            .Select(setting => (string?)setting.Value)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>Booking map UI gate: true only for an explicit "true" value.</summary>
    public static async Task<bool> IsBookingUiEnabledAsync(
        AppDbContext db,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            return false;
        }

        var raw = await ReadValueAsync(db, tenantId, TenantSettingKeys.BookingUiEnabled, cancellationToken);
        return TenantSettingValues.TryParseBoolean(raw, out var enabled) && enabled;
    }

    /// <summary>The subset of <paramref name="requestedIds"/> that are rental assets of the tenant.</summary>
    public static async Task<HashSet<Guid>> ExistingRentalAssetIdsAsync(
        AppDbContext db,
        Guid tenantId,
        IReadOnlyCollection<Guid> requestedIds,
        CancellationToken cancellationToken)
    {
        if (requestedIds.Count == 0)
        {
            return [];
        }

        var wanted = requestedIds.Distinct().ToArray();

        var found = await db.RentalAssets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(asset => asset.TenantId == tenantId && wanted.Contains(asset.Id))
            .Select(asset => asset.Id)
            .ToListAsync(cancellationToken);
        return found.ToHashSet();
    }
}
