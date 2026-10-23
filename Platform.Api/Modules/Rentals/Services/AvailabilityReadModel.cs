using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>
/// Read side of the agenda day: persisted slots plus OpenHours/SlotGrid derivation and
/// reservation occupancy. No writes; <see cref="ScheduleService"/> owns every mutation.
/// </summary>
internal sealed class AvailabilityReadModel(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IOccupancyKindService occupancyKindService,
    TimeProvider timeProvider)
{
    internal static readonly ReservationStatus[] BlockingStatuses =
    [
        ReservationStatus.PendingDeposit,
        ReservationStatus.Confirmed
    ];

    public async Task<DayScheduleResponseDto> GetDayAsync(
        DateOnly date,
        Guid? rentalAssetId,
        bool customerFacing,
        CancellationToken cancellationToken,
        IReadOnlyCollection<Guid>? rentalAssetIds = null)
    {
        var tenantId = EnsureTenant();
        await occupancyKindService.EnsureDefaultsAsync(cancellationToken);

        var requestedIds = ResolveRentableIds(rentalAssetId, rentalAssetIds);
        var ids = requestedIds;
        if (customerFacing)
        {
            ids = await BookingHorizon.ScopeForPublicDayAsync(
                dbContext,
                tenantId,
                date,
                requestedIds,
                timeProvider,
                cancellationToken);

            // Scope returns only active rentable ids still inside the horizon.
            // An empty list is an empty day, including when the caller named no
            // ids and the only persisted slots belong to inactive resources.
            if (ids.Count == 0)
            {
                return new DayScheduleResponseDto(date, []);
            }
        }

        var persistedEntities = await LoadPersistedSlotEntitiesAsync(
            date, ids, includeCancelled: !customerFacing, cancellationToken);
        var persistedStarts = (await LoadPersistedStartsAsync(date, ids, cancellationToken))
            .ToHashSet();

        var templateBySlot = await LoadTemplatesForSlotsAsync(
            date, persistedEntities, cancellationToken);

        var persisted = persistedEntities
            .Select(s => ToSlotDto(
                s,
                isDerived: false,
                ResolvePersistedSource(s, templateBySlot.GetValueOrDefault(s.Id))))
            .ToList();

        var derivedOpenHours = await DeriveOpenHoursSlotsAsync(
            date, ids, persistedStarts, cancellationToken);
        var derivedSlotGrid = await DeriveSlotGridFromTemplatesAsync(
            date, ids, persistedStarts, cancellationToken);

        IEnumerable<SlotResponseDto> all = persisted
            .Concat(derivedOpenHours)
            .Concat(derivedSlotGrid);

        if (customerFacing)
        {
            all = all.Where(s =>
                s.Status == SlotStatus.Available && s.IsBookableByCustomer);
        }

        var slots = all
            .OrderBy(s => s.AssetName)
            .ThenBy(s => s.StartTime)
            .ToList();

        return new DayScheduleResponseDto(date, slots);
    }

    private async Task<List<Slot>> LoadPersistedSlotEntitiesAsync(
        DateOnly date,
        IReadOnlyList<Guid> rentalAssetIds,
        bool includeCancelled,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Slots
            .AsNoTracking()
            .Include(s => s.RentalAsset).ThenInclude(r => r.Asset)
            .Include(s => s.OccupancyKind)
            .Where(s => s.Date == date);

        if (!includeCancelled)
        {
            query = query.Where(s => s.Status != SlotStatus.Cancelled);
        }

        if (rentalAssetIds.Count > 0)
        {
            query = query.Where(s => rentalAssetIds.Contains(s.RentalAssetId));
        }

        return await query.ToListAsync(cancellationToken);
    }

    private async Task<List<(Guid RentalAssetId, TimeOnly StartTime)>> LoadPersistedStartsAsync(
        DateOnly date,
        IReadOnlyList<Guid> rentalAssetIds,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Slots
            .AsNoTracking()
            .Where(s => s.Date == date);

        if (rentalAssetIds.Count > 0)
        {
            query = query.Where(s => rentalAssetIds.Contains(s.RentalAssetId));
        }

        var rows = await query
            .Select(s => new { s.RentalAssetId, s.StartTime })
            .ToListAsync(cancellationToken);
        return rows.Select(row => (row.RentalAssetId, row.StartTime)).ToList();
    }

    private async Task<Dictionary<Guid, ScheduleTemplate>> LoadTemplatesForSlotsAsync(
        DateOnly date,
        IReadOnlyList<Slot> slots,
        CancellationToken cancellationToken)
    {
        var templateIds = slots
            .Select(s => s.SourceTemplateId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        var map = new Dictionary<Guid, ScheduleTemplate>();
        Dictionary<Guid, ScheduleTemplate> byId = [];

        if (templateIds.Count > 0)
        {
            var templates = await dbContext.ScheduleTemplates
                .AsNoTracking()
                .Where(t => templateIds.Contains(t.Id))
                .ToListAsync(cancellationToken);
            byId = templates.ToDictionary(t => t.Id);

            foreach (var slot in slots)
            {
                if (slot.SourceTemplateId is { } tid && byId.TryGetValue(tid, out var template))
                {
                    map[slot.Id] = template;
                }
            }
        }

        var missingAssetIds = slots
            .Where(s => !map.ContainsKey(s.Id))
            .Select(s => s.RentalAssetId)
            .Distinct()
            .ToList();
        if (missingAssetIds.Count > 0)
        {
            var dayTemplates = await dbContext.ScheduleTemplates
                .AsNoTracking()
                .Where(t => missingAssetIds.Contains(t.RentalAssetId)
                            && t.IsActive
                            && t.DayOfWeek == date.DayOfWeek)
                .ToListAsync(cancellationToken);
            foreach (var slot in slots.Where(s => !map.ContainsKey(s.Id)))
            {
                var match = dayTemplates.FirstOrDefault(t =>
                    t.RentalAssetId == slot.RentalAssetId
                    && t.StartTime == slot.StartTime
                    && t.EndTime == slot.EndTime
                    && t.OccupancyKindId == slot.OccupancyKindId);
                if (match is not null)
                {
                    map[slot.Id] = match;
                }
            }
        }

        return map;
    }

    internal static SlotOccurrenceSource ResolvePersistedSource(Slot slot, ScheduleTemplate? template)
    {
        if (slot.Status == SlotStatus.Cancelled)
        {
            return SlotOccurrenceSource.DailyOverride;
        }

        if (template is null)
        {
            return SlotOccurrenceSource.DailyOverride;
        }

        var sameKind = slot.OccupancyKindId == template.OccupancyKindId;
        var sameLabel = TrimLabel(slot.Label) == TrimLabel(template.Label);
        var sameEnd = slot.EndTime == template.EndTime;
        return sameKind && sameLabel && sameEnd
            ? SlotOccurrenceSource.WeeklyDefault
            : SlotOccurrenceSource.DailyOverride;
    }

    private async Task<List<SlotResponseDto>> DeriveOpenHoursSlotsAsync(
        DateOnly date,
        IReadOnlyList<Guid> rentalAssetIds,
        IReadOnlySet<(Guid RentalAssetId, TimeOnly StartTime)> persistedStarts,
        CancellationToken cancellationToken)
    {
        var rentablesQuery = dbContext.RentalAssets
            .AsNoTracking()
            .Include(r => r.Asset)
            .Where(r => r.IsActive
                        && r.SchedulePolicy == SchedulePolicy.OpenHours
                        && r.OpenTime != null
                        && r.CloseTime != null);

        if (rentalAssetIds.Count > 0)
        {
            rentablesQuery = rentablesQuery.Where(r => rentalAssetIds.Contains(r.Id));
        }

        var rentables = await rentablesQuery.ToListAsync(cancellationToken);
        if (rentables.Count == 0)
        {
            return [];
        }

        var openKind = await dbContext.OccupancyKinds
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.Key == "open" && k.IsActive, cancellationToken);

        if (openKind is null)
        {
            return [];
        }

        var reservedWindows = await LoadReservedWindowsAsync(
            EnsureTenant(), date, rentables.Select(r => r.Id).ToList(), cancellationToken);

        var derived = new List<SlotResponseDto>();

        foreach (var rental in rentables)
        {
            var open = rental.OpenTime!.Value;
            var close = rental.CloseTime!.Value;
            if (close <= open)
            {
                continue;
            }

            var windows = reservedWindows.TryGetValue(rental.Id, out var found)
                ? found
                : [];

            var durations = ParseDurations(rental.AllowedDurationMinutes);
            foreach (var minutes in durations)
            {
                var cursor = open;
                while (true)
                {
                    var end = cursor.AddMinutes(minutes);
                    if (end > close || end <= cursor)
                    {
                        break;
                    }

                    if (persistedStarts.Contains((rental.Id, cursor)))
                    {
                        cursor = end;
                        continue;
                    }

                    var startDt = ToDateTime(date, cursor);
                    var endDt = ToDateTime(date, end);
                    var reserved = SumOverlapping(windows, startDt, endDt);

                    var available = rental.Type == RentalAssetType.Location
                        ? reserved == 0
                        : reserved < rental.TotalQuantity;

                    if (available)
                    {
                        derived.Add(new SlotResponseDto(
                            Guid.Empty,
                            rental.Id,
                            rental.Asset.Name,
                            date,
                            cursor,
                            end,
                            openKind.Id,
                            openKind.Key,
                            openKind.Label,
                            openKind.ColorHex,
                            openKind.IsBookableByCustomer,
                            null,
                            SlotStatus.Available,
                            null,
                            IsDerived: true,
                            SlotOccurrenceSource.WeeklyDefault,
                            SourceTemplateId: null,
                            SchedulePolicy.OpenHours,
                            SupportsEntireRecurrence: false));
                    }

                    cursor = end;
                }
            }
        }

        return derived;
    }

    /// <summary>
    /// Unpublished SlotGrid days reuse the weekday's templates as derived windows, the same way
    /// OpenHours derives from open/close. Overlapping kinds are split at breakpoints and the
    /// highest-rank winner occupies each segment. Persisted starts (including cancelled tombstones) win.
    /// </summary>
    private async Task<List<SlotResponseDto>> DeriveSlotGridFromTemplatesAsync(
        DateOnly date,
        IReadOnlyList<Guid> rentalAssetIds,
        IReadOnlySet<(Guid RentalAssetId, TimeOnly StartTime)> persistedStarts,
        CancellationToken cancellationToken)
    {
        var templatesQuery = dbContext.ScheduleTemplates
            .AsNoTracking()
            .Include(t => t.RentalAsset).ThenInclude(r => r.Asset)
            .Include(t => t.OccupancyKind)
            .Where(t => t.IsActive
                        && t.DayOfWeek == date.DayOfWeek
                        && t.RentalAsset.IsActive
                        && t.RentalAsset.SchedulePolicy == SchedulePolicy.SlotGrid);

        if (rentalAssetIds.Count > 0)
        {
            templatesQuery = templatesQuery.Where(t => rentalAssetIds.Contains(t.RentalAssetId));
        }

        var templates = await templatesQuery.ToListAsync(cancellationToken);
        if (templates.Count == 0)
        {
            return [];
        }

        var reservedWindows = await LoadReservedWindowsAsync(
            EnsureTenant(),
            date,
            templates.Select(t => t.RentalAssetId).Distinct().ToList(),
            cancellationToken);

        var derived = new List<SlotResponseDto>();
        foreach (var group in templates.GroupBy(t => t.RentalAssetId))
        {
            foreach (var window in OccupancyPrecedence.SplitWinningWindows(group.ToList()))
            {
                if (persistedStarts.Contains((window.Template.RentalAssetId, window.Start)))
                {
                    continue;
                }

                var startDt = ToDateTime(date, window.Start);
                var endDt = ToDateTime(date, window.End);
                var windows = reservedWindows.TryGetValue(window.Template.RentalAssetId, out var found)
                    ? found
                    : [];
                var reserved = SumOverlapping(windows, startDt, endDt);
                var available = window.Template.RentalAsset.Type == RentalAssetType.Location
                    ? reserved == 0
                    : reserved < window.Template.RentalAsset.TotalQuantity;

                if (!available)
                {
                    continue;
                }

                derived.Add(new SlotResponseDto(
                    Guid.Empty,
                    window.Template.RentalAssetId,
                    window.Template.RentalAsset.Asset.Name,
                    date,
                    window.Start,
                    window.End,
                    window.Template.OccupancyKindId,
                    window.Template.OccupancyKind.Key,
                    window.Template.OccupancyKind.Label,
                    window.Template.OccupancyKind.ColorHex,
                    window.Template.OccupancyKind.IsBookableByCustomer,
                    window.Template.Label,
                    SlotStatus.Available,
                    null,
                    IsDerived: true,
                    SlotOccurrenceSource.WeeklyDefault,
                    SourceTemplateId: window.Template.Id,
                    SchedulePolicy.SlotGrid,
                    SupportsEntireRecurrence: true));
            }
        }

        return derived;
    }

    /// <summary>
    /// Loads every blocking reservation window that touches <paramref name="date"/> in a single
    /// query, so slot derivation can be computed in memory instead of one round trip per slot.
    /// </summary>
    private async Task<Dictionary<Guid, List<ReservedWindow>>> LoadReservedWindowsAsync(
        Guid tenantId,
        DateOnly date,
        IReadOnlyList<Guid> rentalAssetIds,
        CancellationToken cancellationToken)
    {
        if (rentalAssetIds.Count == 0)
        {
            return [];
        }

        var dayStart = BrazilTimeZone.StartOfCivilDay(date);
        var dayEnd = BrazilTimeZone.ExclusiveEndOfCivilDay(date);

        var rows = await (
            from item in dbContext.ReservationItems.AsNoTracking()
            join reservation in dbContext.Reservations.AsNoTracking()
                on item.ReservationId equals reservation.Id
            where item.TenantId == tenantId
                  && reservation.TenantId == tenantId
                  && rentalAssetIds.Contains(item.RentalAssetId)
                  && BlockingStatuses.Contains(reservation.Status)
                  && reservation.StartDateTime < dayEnd
                  && reservation.EndDateTime > dayStart
            select new
            {
                item.RentalAssetId,
                reservation.StartDateTime,
                reservation.EndDateTime,
                item.Quantity,
            }).ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.RentalAssetId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(row => new ReservedWindow(
                        row.StartDateTime, row.EndDateTime, row.Quantity))
                    .ToList());
    }

    private static int SumOverlapping(
        IReadOnlyList<ReservedWindow> windows,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        var total = 0;
        foreach (var window in windows)
        {
            if (window.Start < end && window.End > start)
            {
                total += window.Quantity;
            }
        }

        return total;
    }

    private sealed record ReservedWindow(
        DateTimeOffset Start,
        DateTimeOffset End,
        int Quantity);

    private static IReadOnlyList<int> ParseDurations(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [60];
        }

        var list = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var m) ? m : 0)
            .Where(m => m > 0)
            .Distinct()
            .OrderBy(m => m)
            .ToList();

        return list.Count == 0 ? [60] : list;
    }

    internal static IReadOnlyList<Guid> ResolveRentableIds(
        Guid? rentalAssetId,
        IReadOnlyCollection<Guid>? rentalAssetIds)
    {
        var ids = new List<Guid>();
        if (rentalAssetId is { } single && single != Guid.Empty)
        {
            ids.Add(single);
        }

        if (rentalAssetIds is not null)
        {
            ids.AddRange(rentalAssetIds.Where(id => id != Guid.Empty));
        }

        return ids.Distinct().ToList();
    }

    private Guid EnsureTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");

    internal static string? TrimLabel(string? label) =>
        string.IsNullOrWhiteSpace(label) ? null : label.Trim();

    private static DateTimeOffset ToDateTime(DateOnly date, TimeOnly time) =>
        BrazilTimeZone.AtLocal(date, time);

    internal static SlotResponseDto ToSlotDto(
        Slot s,
        bool isDerived,
        SlotOccurrenceSource source) =>
        new(
            s.Id,
            s.RentalAssetId,
            s.RentalAsset.Asset.Name,
            s.Date,
            s.StartTime,
            s.EndTime,
            s.OccupancyKindId,
            s.OccupancyKind.Key,
            s.OccupancyKind.Label,
            s.OccupancyKind.ColorHex,
            s.OccupancyKind.IsBookableByCustomer,
            s.Label,
            s.Status,
            s.ReservationId,
            isDerived,
            source,
            s.SourceTemplateId,
            s.RentalAsset.SchedulePolicy,
            SupportsEntireRecurrence: s.RentalAsset.SchedulePolicy == SchedulePolicy.SlotGrid);
}
