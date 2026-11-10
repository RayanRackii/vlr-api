using Microsoft.EntityFrameworkCore;
using Platform.Core.Infrastructure.Persistence;
using Platform.Api.Modules.Rentals;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>
/// How far ahead a customer booking must start. Minutes are compared with the
/// injected clock. Null means no minimum. A start equal to now plus the lead is allowed.
/// When several resources are booked together, the longest unmet lead is reported.
/// </summary>
internal static class BookingLeadTime
{
    public static void Ensure(DateTimeOffset start, DateTimeOffset utcNow, int? minutes)
    {
        if (minutes is int lead && start < utcNow.AddMinutes(lead))
        {
            throw new BookingLeadTimeNotMetException(lead);
        }
    }

    public static async Task EnsureAssetsAsync(
        AppDbContext db,
        Guid tenantId,
        IReadOnlyCollection<Guid> assetIds,
        DateTimeOffset start,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        var resolver = new PolicyResolver(db);
        var tenant = await resolver.ReadTenantAsync(tenantId, cancellationToken);
        var categories = await resolver.ReadCategoriesAsync(tenantId, cancellationToken);
        var rows = await db.RentalAssets
            .AsNoTracking()
            .Where(item => item.TenantId == tenantId && assetIds.Contains(item.AssetId) && item.IsActive)
            .Select(item => new { item.MinimumBookingLeadMinutes, item.Asset.CategoryId })
            .ToListAsync(cancellationToken);

        int? blocking = null;
        foreach (var row in rows)
        {
            var lead = PolicyResolver.EffectiveLeadMinutes(
                row.MinimumBookingLeadMinutes,
                categories.GetValueOrDefault(row.CategoryId),
                tenant);
            if (lead is int minutes && start < utcNow.AddMinutes(minutes)
                && (blocking is null || minutes > blocking))
            {
                blocking = minutes;
            }
        }

        if (blocking is int required)
        {
            throw new BookingLeadTimeNotMetException(required);
        }
    }
}
