using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

public readonly record struct SlotGridWindowPlan(
    bool IsOpen,
    IReadOnlyList<(TimeOnly Start, TimeOnly End)> Windows,
    IReadOnlyList<Guid> PersistedSlotIds);

/// <summary>
/// The single authoritative SlotGrid window predicate — persisted Slots (including dated
/// overrides and cancelled cells) take precedence over template winners by StartTime, exactly as
/// <see cref="ReservationService"/> resolves a customer booking's window. Extracted so
/// <see cref="ResourceWorkWindowValidator"/> (M1: planning must validate against the same
/// calendar source as booking, not a duration-only check) never drifts from booking's own rules.
/// </summary>
internal static class SlotGridWindowResolver
{
    /// <summary>
    /// A SlotGrid interval is open iff it is exactly the union of one or more contiguous,
    /// open windows (persisted slots take precedence over template winners by StartTime).
    /// Spanning more than one window additionally requires the total duration to be one of the
    /// rental's AllowedDurationMinutes; when that is unset, only a single window may be booked.
    /// </summary>
    public static async Task<SlotGridWindowPlan> ResolveAsync(
        AppDbContext dbContext,
        Guid tenantId,
        RentalAsset rental,
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        CancellationToken cancellationToken)
    {
        var persistedSlots = await dbContext.Slots
            .AsNoTracking()
            .Include(s => s.OccupancyKind)
            .Where(s => s.TenantId == tenantId && s.RentalAssetId == rental.Id && s.Date == date)
            .ToListAsync(cancellationToken);

        var templates = await dbContext.ScheduleTemplates
            .AsNoTracking()
            .Include(t => t.OccupancyKind)
            .Where(t => t.TenantId == tenantId
                        && t.RentalAssetId == rental.Id
                        && t.IsActive
                        && t.DayOfWeek == date.DayOfWeek)
            .ToListAsync(cancellationToken);
        var templateWindows = OccupancyPrecedence.SplitWinningWindows(templates);

        var normalizedEnd = TenantBookingClock.NormalizeEndTime(endTime);
        var windows = new List<(TimeOnly Start, TimeOnly End)>();
        var persistedSlotIds = new List<Guid>();
        if (persistedSlots.Any(s => s.StartTime < normalizedEnd
            && TenantBookingClock.NormalizeEndTime(s.EndTime) > startTime
            && (s.Status != SlotStatus.Available || !s.OccupancyKind.IsActive
                || !s.OccupancyKind.IsBookableByCustomer)))
        {
            return new SlotGridWindowPlan(false, windows, persistedSlotIds);
        }

        var cursor = startTime;

        while (cursor < normalizedEnd)
        {
            bool open;
            TimeOnly windowEnd;

            var persisted = persistedSlots.FirstOrDefault(s => s.StartTime == cursor);
            if (persisted is not null)
            {
                windowEnd = TenantBookingClock.NormalizeEndTime(persisted.EndTime);
                open = persisted.Status == SlotStatus.Available
                       && persisted.OccupancyKind.IsActive
                       && persisted.OccupancyKind.IsBookableByCustomer;
            }
            else
            {
                var winner = templateWindows.FirstOrDefault(w => w.Start == cursor);
                if (winner.Template is null)
                {
                    return new SlotGridWindowPlan(false, windows, persistedSlotIds);
                }

                windowEnd = TenantBookingClock.NormalizeEndTime(winner.End);
                open = winner.Template.OccupancyKind.IsActive
                       && winner.Template.OccupancyKind.IsBookableByCustomer;
            }

            if (!open || windowEnd <= cursor)
            {
                return new SlotGridWindowPlan(false, windows, persistedSlotIds);
            }

            if (persisted is not null)
                persistedSlotIds.Add(persisted.Id);
            windows.Add((cursor, windowEnd));
            cursor = windowEnd;
        }

        if (cursor != normalizedEnd)
        {
            return new SlotGridWindowPlan(false, windows, persistedSlotIds);
        }

        if (windows.Count > 1)
        {
            if (string.IsNullOrWhiteSpace(rental.AllowedDurationMinutes))
            {
                return new SlotGridWindowPlan(false, windows, persistedSlotIds);
            }

            var totalMinutes = (TenantBookingClock.EndOfDayTime(normalizedEnd) - startTime.ToTimeSpan()).TotalMinutes;
            if (!OpenHoursRules.ParseDurations(rental.AllowedDurationMinutes).Any(minutes => minutes == totalMinutes))
            {
                return new SlotGridWindowPlan(false, windows, persistedSlotIds);
            }
        }

        return new SlotGridWindowPlan(true, windows, persistedSlotIds);
    }
}
