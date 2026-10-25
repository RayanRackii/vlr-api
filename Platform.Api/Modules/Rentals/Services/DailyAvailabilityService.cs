using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

public interface IDailyAvailabilityService
{
    Task<DailyAvailabilityResponseDto> GetAsync(DateOnly date, IReadOnlyCollection<Guid>? rentalAssetIds,
        Guid? unitId, int quantity, int? durationMinutes, CancellationToken cancellationToken);
}

/// <summary>Read-only bulk projection. No seeding, materialization, queue mutation or capacity hold.</summary>
public sealed class DailyAvailabilityService(
    AppDbContext db, ITenantProvider tenantProvider, TimeProvider clock) : IDailyAvailabilityService
{
    public const int MaxResources = 100;
    public const int MaxWindows = 10000;

    public async Task<DailyAvailabilityResponseDto> GetAsync(DateOnly date,
        IReadOnlyCollection<Guid>? rentalAssetIds, Guid? unitId, int quantity, int? durationMinutes,
        CancellationToken cancellationToken)
    {
        if (date == DateOnly.MaxValue || quantity is < 1 or > 1000000 || durationMinutes is < 1 or > 1440
            || unitId == Guid.Empty || rentalAssetIds?.Contains(Guid.Empty) == true)
            throw new AvailabilityRequestException("AVAILABILITY_INVALID_QUERY", "Invalid availability query.");
        if (rentalAssetIds?.Count > MaxResources)
            throw Limit("At most 100 resource IDs may be requested.");
        var tenantId = tenantProvider.TenantId is { } id && id != Guid.Empty
            ? id : throw new UnauthorizedAccessException("Tenant context is required.");
        var now = clock.GetUtcNow();
        var zone = await TenantBookingClock.ResolveForTenantAsync(db, tenantId, cancellationToken);
        var timeZoneId = TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var ianaId) ? ianaId : zone.Id;
        var today = BookingHorizon.CivilToday(now, zone);
        var tenantDays = await BookingHorizon.ReadTenantDaysAsync(db, tenantId, cancellationToken);
        var resourcesQuery = db.RentalAssets.AsNoTracking().Include(r => r.Asset)
            .Where(r => r.TenantId == tenantId && r.Asset.TenantId == tenantId && r.Asset.Unit.TenantId == tenantId
                && r.IsActive && r.Asset.IsRentable);
        if (rentalAssetIds is { Count: > 0 })
            resourcesQuery = resourcesQuery.Where(r => rentalAssetIds.Contains(r.Id));
        if (unitId.HasValue)
            resourcesQuery = resourcesQuery.Where(r => r.Asset.UnitId == unitId.Value);
        var resources = await resourcesQuery.OrderBy(r => r.Id).Take(MaxResources + 1).ToListAsync(cancellationToken);
        if (resources.Count > MaxResources) throw Limit("At most 100 resources may be returned. Narrow the scope.");
        if (resources.Count > 0 && resources.All(r =>
                BookingHorizon.IsBeyond(date, today, BookingHorizon.Effective(r.MaxAdvanceBookingDays, tenantDays))))
            throw new BookingHorizonExceededException();
        if (resources.Count == 0)
        {
            if (rentalAssetIds is not { Count: > 0 } && unitId is null && BookingHorizon.IsBeyond(date, today, tenantDays))
                throw new BookingHorizonExceededException();
            return new(date, timeZoneId, now, now.AddSeconds(60), "BRL", []);
        }
        var ids = resources.Select(r => r.Id).ToList();
        var kinds = await db.OccupancyKinds.AsNoTracking().Where(k => k.TenantId == tenantId)
            .ToDictionaryAsync(k => k.Id, cancellationToken);
        var slots = await db.Slots.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.Date == date && ids.Contains(s.RentalAssetId))
            .Take(MaxWindows + 1).ToListAsync(cancellationToken);
        var templates = await db.ScheduleTemplates.AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.IsActive && t.DayOfWeek == date.DayOfWeek && ids.Contains(t.RentalAssetId))
            .Take(MaxWindows + 1).ToListAsync(cancellationToken);
        if (slots.Count > MaxWindows || templates.Count > MaxWindows) throw Limit("Schedule exceeds the window limit.");
        // A missing/foreign kind is fail-closed and never exposes a private label.
        foreach (var template in templates)
            template.OccupancyKind = kinds.GetValueOrDefault(template.OccupancyKindId) ?? MissingKind(tenantId);
        var prices = await db.RentalPricings.AsNoTracking()
            .Where(p => p.TenantId == tenantId && ids.Contains(p.RentalAssetId) && p.DayOfWeek == date.DayOfWeek)
            .ToListAsync(cancellationToken);
        var (dayStart, dayEnd) = TenantBookingClock.CivilDay(date, zone);
        var reservations = await (
            from item in db.ReservationItems.AsNoTracking()
            join reservation in db.Reservations.AsNoTracking() on item.ReservationId equals reservation.Id
            where item.TenantId == tenantId && reservation.TenantId == tenantId
                && ids.Contains(item.RentalAssetId)
                && AvailabilityReadModel.BlockingStatuses.Contains(reservation.Status)
                && reservation.StartDateTime < dayEnd && reservation.EndDateTime > dayStart
            select new { item.RentalAssetId, item.Quantity, Start = reservation.StartDateTime, End = reservation.EndDateTime })
            .ToListAsync(cancellationToken);

        var result = new List<ResourceAvailabilityDto>();
        var windowCount = 0;
        foreach (var resource in resources.OrderBy(r => r.Asset.Name).ThenBy(r => r.Id))
        {
            var persisted = slots.Where(s => s.RentalAssetId == resource.Id).ToList();
            var candidates = persisted.Select(s => new Window(s.Id, s.StartTime, s.EndTime,
                kinds.GetValueOrDefault(s.OccupancyKindId), s.Status)).ToList();
            var persistedStarts = persisted.Select(s => s.StartTime).ToHashSet();
            if (resource.SchedulePolicy == SchedulePolicy.SlotGrid)
            {
                candidates.AddRange(OccupancyPrecedence.SplitWinningWindows(
                    templates.Where(t => t.RentalAssetId == resource.Id).ToList())
                    .Where(w => !persistedStarts.Contains(w.Start))
                    .Select(w => new Window(null, w.Start, w.End, w.Template.OccupancyKind, SlotStatus.Available)));
            }
            else if (resource.OpenTime is { } open && resource.CloseTime is { } close && close > open)
            {
                var openKind = kinds.Values.FirstOrDefault(k => k.Key == "open" && k.IsActive);
                foreach (var minutes in OpenHoursRules.ParseDurations(resource.AllowedDurationMinutes))
                {
                    var cursor = open;
                    while (true)
                    {
                        var end = cursor.AddMinutes(minutes);
                        if (end <= cursor || end > close) break;
                        if (!persistedStarts.Contains(cursor))
                            candidates.Add(new(null, cursor, end, openKind, SlotStatus.Available));
                        cursor = end;
                    }
                }
            }
            var windows = new List<AvailabilityWindowDto>();
            var effectiveDays = BookingHorizon.Effective(resource.MaxAdvanceBookingDays, tenantDays);
            foreach (var window in candidates.OrderBy(w => w.Start).ThenBy(w => w.End).ThenBy(w => w.Id))
            {
                if (window.End <= window.Start || (durationMinutes.HasValue
                        && (window.End - window.Start).TotalMinutes != durationMinutes.Value)) continue;
                if (++windowCount > MaxWindows) throw Limit("At most 10000 windows may be returned. Narrow the scope.");
                var start = TenantBookingClock.AtLocal(date, window.Start, zone);
                var end = TenantBookingClock.AtLocal(date, window.End, zone);
                if (end <= start) continue;
                var reserved = reservations.Where(r => r.RentalAssetId == resource.Id && r.Start < end && r.End > start)
                    .Sum(r => (long)r.Quantity);
                var totalQuantity = resource.Type == RentalAssetType.Location ? 1 : resource.TotalQuantity;
                var available = (int)Math.Clamp((long)totalQuantity - reserved, 0L, Math.Max(0, totalQuantity));
                var blockingOverlap = persisted.Any(s => s.Id != window.Id
                    && OccupancyPrecedence.IntervalsOverlap(s.StartTime, s.EndTime, window.Start, window.End)
                    && (s.Status == SlotStatus.Cancelled || !kinds.TryGetValue(s.OccupancyKindId, out var kind)
                        || !kind.IsActive || (kind.BlocksCapacity && !kind.IsBookableByCustomer)));
                var blocked = window.Status == SlotStatus.Cancelled || window.Kind is not { IsActive: true, IsBookableByCustomer: true }
                    || blockingOverlap;
                if (blockingOverlap || window.Status == SlotStatus.Cancelled || start < now
                    || window.Kind is { BlocksCapacity: true, IsBookableByCustomer: false }) available = 0;
                var matching = prices.Where(p => p.RentalAssetId == resource.Id
                    && p.StartTime <= window.Start && p.EndTime >= window.End).AsQueryable();
                var hourly = (window.Id.HasValue ? BookingOffer.ForPersistedSlot(matching) : BookingOffer.ForDerivedWindow(matching))
                    .Select(p => (decimal?)p.PricePerHour).FirstOrDefault();
                var amount = hourly.HasValue ? BookingOffer.Total(hourly.Value, (decimal)(end - start).TotalHours, quantity) : (decimal?)null;
                var state = BookingHorizon.IsBeyond(date, today, effectiveDays) ? AvailabilityWindowState.OutsideHorizon
                    : start < now ? AvailabilityWindowState.Past
                    : blocked ? AvailabilityWindowState.Blocked
                    : window.Status == SlotStatus.Booked || available < quantity ? AvailabilityWindowState.Taken
                    : amount is null ? AvailabilityWindowState.Unpriced : AvailabilityWindowState.Available;
                var reason = state switch
                {
                    AvailabilityWindowState.OutsideHorizon => "booking_horizon_exceeded",
                    AvailabilityWindowState.Past => "START_IN_THE_PAST",
                    AvailabilityWindowState.Blocked => "SCHEDULE_BLOCKED",
                    AvailabilityWindowState.Taken => "INSUFFICIENT_CAPACITY",
                    AvailabilityWindowState.Unpriced => "PRICE_UNAVAILABLE",
                    _ => null,
                };
                windows.Add(new(window.Id, window.Start, window.End, !window.Id.HasValue, state, reason, available, amount));
            }
            result.Add(new(resource.Id, resource.AssetId, resource.Asset.UnitId, resource.Asset.Name,
                resource.Type.ToString(), resource.Type == RentalAssetType.Location ? 1 : resource.TotalQuantity,
                resource.RequiresDeposit, resource.Type == RentalAssetType.Location && resource.QueueEnabled, effectiveDays, windows));
        }
        return new(date, timeZoneId, now, now.AddSeconds(60), "BRL", result);
    }

    private static AvailabilityRequestException Limit(string message) => new("AVAILABILITY_LIMIT_EXCEEDED", message);
    private static OccupancyKind MissingKind(Guid tenantId) => new()
    {
        TenantId = tenantId, Key = "unconfigured", Label = "Unavailable", IsActive = false,
        BlocksCapacity = true, IsBookableByCustomer = false, SortOrder = 0,
    };
    private sealed record Window(Guid? Id, TimeOnly Start, TimeOnly End, OccupancyKind? Kind, SlotStatus Status);
}
