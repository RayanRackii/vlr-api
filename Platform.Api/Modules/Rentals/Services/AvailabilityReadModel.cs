using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

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
    // Completing a reservation never releases its time.
    internal static readonly ReservationStatus[] BlockingStatuses =
    [
        ReservationStatus.PendingDeposit,
        ReservationStatus.Confirmed,
        ReservationStatus.Completed
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

        var zone = await TenantBookingClock.ResolveForTenantAsync(dbContext, tenantId, cancellationToken);
        persistedEntities = persistedEntities
            .Where(slot =>
                TenantBookingClock.EndAtLocal(slot.Date, slot.EndTime, zone)
                > TenantBookingClock.AtLocal(slot.Date, slot.StartTime, zone))
            .ToList();

        var templateBySlot = await LoadTemplatesForSlotsAsync(
            date, persistedEntities, cancellationToken);

        var persisted = persistedEntities
            .Select(s => ToSlotDto(
                s,
                isDerived: false,
                ResolvePersistedSource(s, templateBySlot.GetValueOrDefault(s.Id))))
            .ToList();
        var persistedBookedReservations = persistedEntities
            .Where(slot => slot.Status == SlotStatus.Booked && slot.ReservationId is not null)
            .Select(slot => (slot.RentalAssetId, ReservationId: slot.ReservationId!.Value))
            .ToHashSet();

        var derivedOpenHours = await DeriveOpenHoursSlotsAsync(
            date, ids, persistedStarts, persistedBookedReservations, !customerFacing, zone, cancellationToken);
        var derivedSlotGrid = await DeriveSlotGridFromTemplatesAsync(
            date, ids, persistedStarts, persistedBookedReservations, !customerFacing, zone, cancellationToken,
            persistedEntities);

        IEnumerable<SlotResponseDto> all = persisted
            .Concat(derivedOpenHours)
            .Concat(derivedSlotGrid);

        if (customerFacing)
        {
            var reservedWindows = await LoadReservedWindowsAsync(
                tenantId, date, persistedEntities.Select(s => s.RentalAssetId).Distinct().ToList(),
                zone, cancellationToken);
            var availablePersistedIds = persistedEntities.Where(slot =>
            {
                var reserved = reservedWindows.TryGetValue(slot.RentalAssetId, out var windows)
                    ? SumOverlapping(windows,
                        TenantBookingClock.AtLocal(slot.Date, slot.StartTime, zone),
                        TenantBookingClock.EndAtLocal(slot.Date, slot.EndTime, zone))
                    : 0;
                return slot.RentalAsset.Type == RentalAssetType.Location
                    ? reserved == 0
                    : reserved < slot.RentalAsset.TotalQuantity;
            }).Select(slot => slot.Id).ToHashSet();

            all = all.Where(s =>
                s.Status == SlotStatus.Available && s.IsBookableByCustomer
                && (s.IsDerived || availablePersistedIds.Contains(s.Id))
                && !ReservationBookingRules.StartsBeforeNow(
                    TenantBookingClock.AtLocal(s.Date, s.StartTime, zone), timeProvider));
        }

        if (!customerFacing)
        {
            all = await AttachBookingDetailsAsync(tenantId, all.ToList(), cancellationToken);
        }

        var slots = all
            .OrderBy(s => s.AssetName)
            .ThenBy(s => s.StartTime)
            .ToList();

        return new DayScheduleResponseDto(date, slots);
    }

    /// <summary>
    /// Staff day view: stamp booked cells with the reservation item quantity for that resource
    /// and the customer name, so multi-unit occupancy counts units rather than bookings.
    /// </summary>
    private async Task<List<SlotResponseDto>> AttachBookingDetailsAsync(
        Guid tenantId,
        List<SlotResponseDto> slots,
        CancellationToken cancellationToken)
    {
        var reservationIds = slots
            .Where(s => s.Status == SlotStatus.Booked && s.ReservationId is not null)
            .Select(s => s.ReservationId!.Value)
            .Distinct()
            .ToList();
        if (reservationIds.Count == 0)
        {
            return slots;
        }

        var rows = await (
            from item in dbContext.ReservationItems.AsNoTracking()
            join reservation in dbContext.Reservations.AsNoTracking()
                on item.ReservationId equals reservation.Id
            where item.TenantId == tenantId
                  && reservation.TenantId == tenantId
                  && reservationIds.Contains(reservation.Id)
            select new { reservation.Id, item.RentalAssetId, item.Quantity, reservation.CustomerName })
            .ToListAsync(cancellationToken);

        var quantities = rows
            .GroupBy(r => (r.Id, r.RentalAssetId))
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Quantity));
        var names = rows
            .GroupBy(r => r.Id)
            .ToDictionary(g => g.Key, g => g.First().CustomerName);

        return slots
            .Select(s => s.Status == SlotStatus.Booked && s.ReservationId is { } rid
                ? s with
                {
                    Quantity = quantities.TryGetValue((rid, s.RentalAssetId), out var q) ? q : null,
                    CustomerName = names.GetValueOrDefault(rid),
                }
                : s)
            .ToList();
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
        IReadOnlySet<(Guid RentalAssetId, Guid ReservationId)> persistedBookedReservations,
        bool includeBookedReservations,
        TimeZoneInfo zone,
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
            EnsureTenant(), date, rentables.Select(r => r.Id).ToList(), zone, cancellationToken);

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
                    var endOffset = cursor.ToTimeSpan().Add(TimeSpan.FromMinutes(minutes));
                    if (endOffset > TenantBookingClock.EndOfDayTime(close)) break;
                    var end = endOffset == TimeSpan.FromDays(1)
                        ? TenantBookingClock.DayClose : TimeOnly.FromTimeSpan(endOffset);
                    if (end <= cursor)
                    {
                        break;
                    }

                    if (persistedStarts.Contains((rental.Id, cursor)))
                    {
                        cursor = end;
                        continue;
                    }

                    var startDt = TenantBookingClock.AtLocal(date, cursor, zone);
                    var endDt = TenantBookingClock.EndAtLocal(date, end, zone);
                    if (endDt <= startDt)
                    {
                        cursor = end;
                        continue;
                    }

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

            if (includeBookedReservations)
            {
                AppendDerivedBookedReservations(
                    derived, date, zone, rental.Id, rental.Asset.Name, windows, openKind,
                    persistedBookedReservations, SchedulePolicy.OpenHours, supportsEntireRecurrence: false);
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
        IReadOnlySet<(Guid RentalAssetId, Guid ReservationId)> persistedBookedReservations,
        bool includeBookedReservations,
        TimeZoneInfo zone,
        CancellationToken cancellationToken,
        IReadOnlyList<Slot> persistedSlots)
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
            zone,
            cancellationToken);

        var openKind = includeBookedReservations
            ? await dbContext.OccupancyKinds
                .AsNoTracking()
                .FirstOrDefaultAsync(k => k.Key == "open" && k.IsActive, cancellationToken)
            : null;

        var derived = new List<SlotResponseDto>();
        foreach (var group in templates.GroupBy(t => t.RentalAssetId))
        {
            var rental = group.First().RentalAsset;
            var templateWindows = OccupancyPrecedence.SplitWinningWindows(group.ToList());
            foreach (var window in templateWindows)
            {
                if (persistedStarts.Contains((window.Template.RentalAssetId, window.Start)))
                {
                    continue;
                }

                var startDt = TenantBookingClock.AtLocal(date, window.Start, zone);
                var endDt = TenantBookingClock.EndAtLocal(date, window.End, zone);
                if (endDt <= startDt)
                {
                    continue;
                }

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

            if (openKind is not null && includeBookedReservations)
            {
                var windows = reservedWindows.TryGetValue(rental.Id, out var found)
                    ? found
                    : [];
                var gridCells = templateWindows
                    .Where(w => !persistedStarts.Contains((rental.Id, w.Start)))
                    .Select(w => (w.Start, w.End))
                    .ToList();
                AppendDerivedBookedReservations(
                    derived, date, zone, rental.Id, rental.Asset.Name, windows, openKind,
                    persistedBookedReservations, SchedulePolicy.SlotGrid, supportsEntireRecurrence: true,
                    gridCells, persistedSlots.Where(s => s.RentalAssetId == rental.Id
                        && s.Status == SlotStatus.Booked).ToList());
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
        TimeZoneInfo zone,
        CancellationToken cancellationToken)
    {
        if (rentalAssetIds.Count == 0)
        {
            return [];
        }

        var (dayStart, dayEnd) = TenantBookingClock.CivilDay(date, zone);

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
                reservation.Id,
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
                        row.Id, row.StartDateTime, row.EndDateTime, row.Quantity))
                    .ToList());
    }

    private static void AppendDerivedBookedReservations(
        ICollection<SlotResponseDto> derived,
        DateOnly date,
        TimeZoneInfo zone,
        Guid rentalAssetId,
        string assetName,
        IReadOnlyList<ReservedWindow> windows,
        OccupancyKind openKind,
        IReadOnlySet<(Guid RentalAssetId, Guid ReservationId)> persistedBookedReservations,
        SchedulePolicy schedulePolicy,
        bool supportsEntireRecurrence,
        IReadOnlyList<(TimeOnly Start, TimeOnly End)>? gridCells = null,
        IReadOnlyList<Slot>? persistedBookedSlots = null)
    {
        var (dayStart, dayEnd) = TenantBookingClock.CivilDay(date, zone);
        foreach (var window in windows)
        {
            if (gridCells is null && persistedBookedReservations.Contains((rentalAssetId, window.ReservationId)))
            {
                continue;
            }

            var start = window.Start > dayStart ? window.Start : dayStart;
            var end = window.End < dayEnd ? window.End : dayEnd;
            if (end <= start)
            {
                continue;
            }

            var startTimeOnly = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, zone).DateTime);
            var endTimeOnly = end >= dayEnd
                ? TenantBookingClock.DayClose
                : TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(end, zone).DateTime);

            if (gridCells is not null)
            {
                var persistedCoverage = (persistedBookedSlots ?? [])
                    .Where(s => s.ReservationId == window.ReservationId)
                    .Select(s => (Start: s.StartTime, End: TenantBookingClock.NormalizeEndTime(s.EndTime)))
                    .ToList();
                var boundaries = gridCells.SelectMany(c => new[] { c.Start, TenantBookingClock.NormalizeEndTime(c.End) })
                    .Concat(persistedCoverage.SelectMany(c => new[] { c.Start, c.End }))
                    .Where(t => t > startTimeOnly && t < endTimeOnly)
                    .Append(startTimeOnly).Append(endTimeOnly).Distinct().Order().ToList();
                // Keep the whole reservation after template edits, subtracting persisted coverage.
                for (var i = 1; i < boundaries.Count; i++)
                {
                    var cellStart = boundaries[i - 1];
                    var cellEnd = boundaries[i];
                    if (!persistedCoverage.Any(c => c.Start < cellEnd && c.End > cellStart))
                        AppendBookedCell(cellStart, cellEnd);
                }
                continue;
            }

            AppendBookedCell(startTimeOnly, endTimeOnly);

            void AppendBookedCell(TimeOnly cellStart, TimeOnly cellEnd) => derived.Add(new SlotResponseDto(
                Guid.Empty,
                rentalAssetId,
                assetName,
                date,
                cellStart,
                cellEnd,
                openKind.Id,
                openKind.Key,
                openKind.Label,
                openKind.ColorHex,
                false,
                null,
                SlotStatus.Booked,
                window.ReservationId,
                true,
                SlotOccurrenceSource.WeeklyDefault,
                null,
                schedulePolicy,
                supportsEntireRecurrence));
        }
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
        Guid ReservationId,
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
