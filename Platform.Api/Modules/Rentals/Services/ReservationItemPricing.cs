using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>Display metadata from current rules; reservation items do not snapshot pricing units.</summary>
internal sealed record ReservationItemPricing(PricingUnit? Unit, int? Days)
{
    public static async Task<IReadOnlyDictionary<Guid, ReservationItemPricing>> ReadAsync(
        AppDbContext db, IReadOnlyCollection<Reservation> reservations, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, ReservationItemPricing>();
        foreach (var group in reservations.GroupBy(r => r.TenantId))
        {
            var tenantId = group.Key;
            var assetIds = group.SelectMany(r => r.Items)
                .Where(i => i.TenantId == tenantId).Select(i => i.RentalAssetId).Distinct().ToArray();
            var rules = await db.RentalPricings.AsNoTracking()
                .Where(p => p.TenantId == tenantId && assetIds.Contains(p.RentalAssetId))
                .ToListAsync(cancellationToken);
            var zone = await TenantBookingClock.ResolveForTenantAsync(db, tenantId, cancellationToken);
            foreach (var reservation in group)
            {
                var start = TimeZoneInfo.ConvertTime(reservation.StartDateTime, zone);
                var end = TimeZoneInfo.ConvertTime(reservation.EndDateTime, zone);
                var lastDate = end.TimeOfDay == TimeSpan.Zero ? end.Date.AddDays(-1) : end.Date;
                foreach (var item in reservation.Items.Where(i => i.TenantId == tenantId))
                {
                    var units = new HashSet<PricingUnit>();
                    var missingRule = false;
                    for (var date = start.Date; date <= lastDate; date = date.AddDays(1))
                    {
                        var open = item.RentalAsset is { SchedulePolicy: SchedulePolicy.OpenHours, OpenTime: { } openTime }
                            ? openTime.ToTimeSpan() : TimeSpan.Zero;
                        var close = item.RentalAsset is { SchedulePolicy: SchedulePolicy.OpenHours, CloseTime: { } closeTime }
                            ? TenantBookingClock.EndOfDayTime(closeTime) : TimeSpan.FromDays(1);
                        var from = date == start.Date ? start.TimeOfDay : open;
                        var to = date == end.Date ? end.TimeOfDay : close;
                        var matching = rules.Where(p => p.RentalAssetId == item.RentalAssetId
                            && p.DayOfWeek == date.DayOfWeek
                            && p.StartTime.ToTimeSpan() < to
                            && TenantBookingClock.EndOfDayTime(p.EndTime) > from)
                            .OrderBy(p => p.StartTime).ToList();
                        var coveredUntil = from;
                        foreach (var rule in matching)
                        {
                            if (rule.StartTime.ToTimeSpan() > coveredUntil) break;
                            var ruleEnd = TenantBookingClock.EndOfDayTime(rule.EndTime);
                            if (ruleEnd > coveredUntil) coveredUntil = ruleEnd;
                        }
                        missingRule |= matching.Count == 0 || coveredUntil < to;
                        units.UnionWith(matching.Select(p => p.PricingUnit));
                    }
                    PricingUnit? unit = !missingRule && units.Count == 1 ? units.Single() : null;
                    int? days = unit == PricingUnit.Day
                        ? Math.Max(1, (int)Math.Ceiling((reservation.EndDateTime - reservation.StartDateTime).TotalHours / 24))
                        : null;
                    result[item.Id] = new(unit, days);
                }
            }
        }
        return result;
    }
}
