using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Services.Trial;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

public sealed class ScheduleService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IOccupancyKindService occupancyKindService,
    ITrialGuard trialGuard) : IScheduleService
{
    private static readonly ReservationStatus[] BlockingStatuses =
    [
        ReservationStatus.PendingDeposit,
        ReservationStatus.Confirmed
    ];

    public async Task<IReadOnlyList<ScheduleTemplateResponseDto>> ListTemplatesAsync(
        Guid? rentalAssetId,
        CancellationToken cancellationToken,
        IReadOnlyCollection<Guid>? rentalAssetIds = null,
        DayOfWeek? dayOfWeek = null)
    {
        EnsureTenant();

        var ids = ResolveRentableIds(rentalAssetId, rentalAssetIds);

        var query = dbContext.ScheduleTemplates
            .AsNoTracking()
            .Include(t => t.RentalAsset).ThenInclude(r => r.Asset)
            .Include(t => t.OccupancyKind)
            .AsQueryable();

        if (ids.Count > 0)
        {
            query = query.Where(t => ids.Contains(t.RentalAssetId));
        }

        if (dayOfWeek is { } day)
        {
            query = query.Where(t => t.DayOfWeek == day);
        }

        var items = await query
            .OrderBy(t => t.RentalAsset.Asset.Name)
            .ThenBy(t => t.DayOfWeek)
            .ThenBy(t => t.StartTime)
            .ToListAsync(cancellationToken);

        return items.Select(ToTemplateDto).ToList();
    }

    public async Task<ScheduleTemplateResponseDto> CreateTemplateAsync(
        UpsertScheduleTemplateRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        ValidateTimeRange(request.StartTime, request.EndTime);
        await EnsureRentableAsync(request.RentalAssetId, cancellationToken);
        await EnsureOccupancyKindAsync(request.OccupancyKindId, cancellationToken);

        var entity = new ScheduleTemplate
        {
            TenantId = tenantId,
            RentalAssetId = request.RentalAssetId,
            DayOfWeek = request.DayOfWeek,
            StartTime = request.StartTime,
            EndTime = request.EndTime,
            OccupancyKindId = request.OccupancyKindId,
            Label = TrimLabel(request.Label),
            IsActive = request.IsActive,
        };

        dbContext.ScheduleTemplates.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        return await GetTemplateDtoAsync(entity.Id, cancellationToken);
    }

    public async Task<ScheduleTemplateResponseDto> UpdateTemplateAsync(
        Guid id,
        UpsertScheduleTemplateRequestDto request,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        ValidateTimeRange(request.StartTime, request.EndTime);

        var entity = await dbContext.ScheduleTemplates
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Schedule template was not found.");

        await EnsureRentableAsync(request.RentalAssetId, cancellationToken);
        await EnsureOccupancyKindAsync(request.OccupancyKindId, cancellationToken);

        entity.RentalAssetId = request.RentalAssetId;
        entity.DayOfWeek = request.DayOfWeek;
        entity.StartTime = request.StartTime;
        entity.EndTime = request.EndTime;
        entity.OccupancyKindId = request.OccupancyKindId;
        entity.Label = TrimLabel(request.Label);
        entity.IsActive = request.IsActive;
        entity.Touch();

        await dbContext.SaveChangesAsync(cancellationToken);
        return await GetTemplateDtoAsync(entity.Id, cancellationToken);
    }

    public async Task DeleteTemplateAsync(Guid id, CancellationToken cancellationToken)
    {
        EnsureTenant();
        var entity = await dbContext.ScheduleTemplates
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Schedule template was not found.");

        dbContext.ScheduleTemplates.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<SeedDefaultTemplatesResponseDto> SeedDefaultTemplatesAsync(
        SeedDefaultTemplatesRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        await occupancyKindService.EnsureDefaultsAsync(cancellationToken);

        var rentableIds = ResolveRentableIds(request.RentalAssetId, request.RentalAssetIds);
        await EnsureRentablesAsync(rentableIds, cancellationToken);

        var open = request.OpenTime ?? new TimeOnly(8, 0);
        var close = request.CloseTime ?? new TimeOnly(22, 0);
        var slotMinutes = request.SlotMinutes <= 0 ? 60 : request.SlotMinutes;

        if (close <= open)
        {
            throw new ArgumentException("CloseTime must be after OpenTime.");
        }

        if (open.AddMinutes(slotMinutes) > close)
        {
            throw new ArgumentException("SlotMinutes must fit within the open interval.");
        }

        OccupancyKind openKind;
        if (request.OccupancyKindId is { } kindId)
        {
            openKind = await dbContext.OccupancyKinds
                .FirstOrDefaultAsync(k => k.Id == kindId && k.IsActive, cancellationToken)
                ?? throw new KeyNotFoundException("Occupancy kind was not found.");
        }
        else
        {
            openKind = await dbContext.OccupancyKinds
                .FirstOrDefaultAsync(
                    k => k.Key == "open" && k.IsActive && k.IsBookableByCustomer,
                    cancellationToken)
                ?? await dbContext.OccupancyKinds
                    .FirstOrDefaultAsync(
                        k => k.IsActive && k.IsBookableByCustomer,
                        cancellationToken)
                ?? throw new InvalidOperationException(
                    "No bookable occupancy kind is available for the default grid.");
        }

        var existing = await dbContext.ScheduleTemplates
            .Where(t => rentableIds.Contains(t.RentalAssetId))
            .Select(t => new { t.RentalAssetId, t.DayOfWeek, t.StartTime, t.EndTime })
            .ToListAsync(cancellationToken);

        var existingKeys = existing
            .Select(row => $"{row.RentalAssetId}|{row.DayOfWeek}|{row.StartTime}|{row.EndTime}")
            .ToHashSet();

        var created = 0;
        var skipped = 0;

        foreach (var rentableId in rentableIds)
        {
            foreach (DayOfWeek dayOfWeek in Enum.GetValues<DayOfWeek>())
            {
                var cursor = open;
                while (true)
                {
                    var end = cursor.AddMinutes(slotMinutes);
                    if (end > close)
                    {
                        break;
                    }

                    var key = $"{rentableId}|{dayOfWeek}|{cursor}|{end}";
                    if (existingKeys.Contains(key))
                    {
                        skipped++;
                        cursor = end;
                        continue;
                    }

                    dbContext.ScheduleTemplates.Add(new ScheduleTemplate
                    {
                        TenantId = tenantId,
                        RentalAssetId = rentableId,
                        DayOfWeek = dayOfWeek,
                        StartTime = cursor,
                        EndTime = end,
                        OccupancyKindId = openKind.Id,
                        Label = null,
                        IsActive = true,
                    });
                    existingKeys.Add(key);
                    created++;
                    cursor = end;
                }
            }
        }

        var policyChanged = await EnsureSlotGridPolicyAsync(rentableIds, cancellationToken);

        if (created > 0 || policyChanged)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new SeedDefaultTemplatesResponseDto(created, skipped);
    }

    public async Task<ApplyWeeklyRuleResponseDto> ApplyWeeklyRuleAsync(
        ApplyWeeklyRuleRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        await occupancyKindService.EnsureDefaultsAsync(cancellationToken);

        if (request.RentalAssetIds is null || request.RentalAssetIds.Count == 0)
        {
            throw new ArgumentException("At least one rentable is required.");
        }

        if (request.DaysOfWeek is null || request.DaysOfWeek.Count == 0)
        {
            throw new ArgumentException("At least one weekday is required.");
        }

        ValidateTimeRange(request.OpenTime, request.CloseTime);
        var slotMinutes = request.SlotMinutes <= 0 ? 60 : request.SlotMinutes;
        if (request.OpenTime.AddMinutes(slotMinutes) > request.CloseTime)
        {
            throw new ArgumentException("SlotMinutes must fit within the open interval.");
        }

        var rentableIds = request.RentalAssetIds.Where(id => id != Guid.Empty).Distinct().ToList();
        await EnsureRentablesAsync(rentableIds, cancellationToken);
        await EnsureOccupancyKindAsync(request.OccupancyKindId, cancellationToken);
        var label = TrimLabel(request.Label);

        var existing = await dbContext.ScheduleTemplates
            .Where(t => rentableIds.Contains(t.RentalAssetId)
                        && request.DaysOfWeek.Contains(t.DayOfWeek))
            .ToListAsync(cancellationToken);

        var byKey = existing.ToDictionary(
            t => $"{t.RentalAssetId}|{t.DayOfWeek}|{t.StartTime}");

        var created = 0;
        var updated = 0;
        var skipped = 0;

        foreach (var rentableId in rentableIds)
        {
            foreach (var dayOfWeek in request.DaysOfWeek.Distinct())
            {
                var cursor = request.OpenTime;
                while (true)
                {
                    var end = cursor.AddMinutes(slotMinutes);
                    if (end > request.CloseTime)
                    {
                        break;
                    }

                    var key = $"{rentableId}|{dayOfWeek}|{cursor}";
                    if (byKey.TryGetValue(key, out var template))
                    {
                        if (template.EndTime == end
                            && template.OccupancyKindId == request.OccupancyKindId
                            && TrimLabel(template.Label) == label
                            && template.IsActive == request.IsActive)
                        {
                            skipped++;
                        }
                        else
                        {
                            template.EndTime = end;
                            template.OccupancyKindId = request.OccupancyKindId;
                            template.Label = label;
                            template.IsActive = request.IsActive;
                            template.Touch();
                            updated++;
                        }
                    }
                    else
                    {
                        var entity = new ScheduleTemplate
                        {
                            TenantId = tenantId,
                            RentalAssetId = rentableId,
                            DayOfWeek = dayOfWeek,
                            StartTime = cursor,
                            EndTime = end,
                            OccupancyKindId = request.OccupancyKindId,
                            Label = label,
                            IsActive = request.IsActive,
                        };
                        dbContext.ScheduleTemplates.Add(entity);
                        byKey[key] = entity;
                        created++;
                    }

                    cursor = end;
                }
            }
        }

        var policyChanged = await EnsureSlotGridPolicyAsync(rentableIds, cancellationToken);

        if (created > 0 || updated > 0 || policyChanged)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new ApplyWeeklyRuleResponseDto(created, updated, skipped);
    }

    public async Task<DayScheduleResponseDto> GetDayAsync(
        DateOnly date,
        Guid? rentalAssetId,
        bool customerFacing,
        CancellationToken cancellationToken,
        IReadOnlyCollection<Guid>? rentalAssetIds = null)
    {
        EnsureTenant();
        await occupancyKindService.EnsureDefaultsAsync(cancellationToken);

        var ids = ResolveRentableIds(rentalAssetId, rentalAssetIds);

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

    public async Task<int> PublishDayAsync(
        PublishDayRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        await occupancyKindService.EnsureDefaultsAsync(cancellationToken);

        var dayOfWeek = request.Date.DayOfWeek;
        var ids = ResolveRentableIds(request.RentalAssetId, request.RentalAssetIds);

        var templatesQuery = dbContext.ScheduleTemplates
            .Include(t => t.RentalAsset)
            .Where(t => t.IsActive && t.DayOfWeek == dayOfWeek);

        if (ids.Count > 0)
        {
            templatesQuery = templatesQuery.Where(t => ids.Contains(t.RentalAssetId));
        }
        else
        {
            templatesQuery = templatesQuery.Where(t =>
                t.RentalAsset.IsActive
                && t.RentalAsset.SchedulePolicy == SchedulePolicy.SlotGrid);
        }

        var templates = await templatesQuery.ToListAsync(cancellationToken);
        var created = 0;

        var templateRentableIds = templates
            .Select(t => t.RentalAssetId)
            .Distinct()
            .ToList();

        var existingStarts = (await dbContext.Slots
                .AsNoTracking()
                .Where(s => s.Date == request.Date
                            && templateRentableIds.Contains(s.RentalAssetId))
                .Select(s => new { s.RentalAssetId, s.StartTime })
                .ToListAsync(cancellationToken))
            .Select(row => (row.RentalAssetId, row.StartTime))
            .ToHashSet();

        foreach (var template in templates)
        {
            if (!existingStarts.Add((template.RentalAssetId, template.StartTime)))
            {
                continue;
            }

            dbContext.Slots.Add(new Slot
            {
                TenantId = tenantId,
                RentalAssetId = template.RentalAssetId,
                Date = request.Date,
                StartTime = template.StartTime,
                EndTime = template.EndTime,
                OccupancyKindId = template.OccupancyKindId,
                Label = template.Label,
                Status = SlotStatus.Available,
                SourceTemplateId = template.Id,
            });
            created++;
        }

        if (created > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return created;
    }

    public async Task<SlotResponseDto> UpsertSlotAsync(
        UpsertSlotRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        ValidateTimeRange(request.StartTime, request.EndTime);
        await EnsureRentableAsync(request.RentalAssetId, cancellationToken);
        await EnsureOccupancyKindAsync(request.OccupancyKindId, cancellationToken);

        var existing = await dbContext.Slots
            .Include(s => s.RentalAsset).ThenInclude(r => r.Asset)
            .Include(s => s.OccupancyKind)
            .FirstOrDefaultAsync(
                s => s.RentalAssetId == request.RentalAssetId
                     && s.Date == request.Date
                     && s.StartTime == request.StartTime,
                cancellationToken);

        if (existing is not null)
        {
            if (existing.Status == SlotStatus.Booked)
            {
                throw new InvalidOperationException("Cannot edit a booked slot.");
            }

            existing.EndTime = request.EndTime;
            existing.OccupancyKindId = request.OccupancyKindId;
            existing.Label = TrimLabel(request.Label);
            existing.Status = SlotStatus.Available;
            existing.Touch();
            await dbContext.SaveChangesAsync(cancellationToken);

            await dbContext.Entry(existing).Reference(s => s.OccupancyKind).LoadAsync(cancellationToken);
            var template = existing.SourceTemplateId is { } tid
                ? await dbContext.ScheduleTemplates.AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == tid, cancellationToken)
                : null;
            return ToSlotDto(existing, isDerived: false, ResolvePersistedSource(existing, template));
        }

        var slot = new Slot
        {
            TenantId = tenantId,
            RentalAssetId = request.RentalAssetId,
            Date = request.Date,
            StartTime = request.StartTime,
            EndTime = request.EndTime,
            OccupancyKindId = request.OccupancyKindId,
            Label = TrimLabel(request.Label),
            Status = SlotStatus.Available,
        };

        dbContext.Slots.Add(slot);
        await dbContext.SaveChangesAsync(cancellationToken);

        return await GetSlotDtoAsync(slot.Id, cancellationToken);
    }

    public async Task CancelSlotAsync(Guid slotId, CancellationToken cancellationToken)
    {
        EnsureTenant();
        var slot = await dbContext.Slots
            .FirstOrDefaultAsync(s => s.Id == slotId, cancellationToken)
            ?? throw new KeyNotFoundException("Slot was not found.");

        if (slot.Status == SlotStatus.Booked)
        {
            throw new InvalidOperationException("Cannot cancel a booked slot; cancel the reservation instead.");
        }

        slot.MarkCancelled();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<SlotResponseDto> ApplyDailyOccurrenceAsync(
        ApplyDailyOccurrenceRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        ValidateTimeRange(request.StartTime, request.EndTime);
        await EnsureRentableAsync(request.RentalAssetId, cancellationToken);

        var slot = await ResolveOccurrenceSlotAsync(request, cancellationToken);

        if (slot is not null && slot.Status == SlotStatus.Booked)
        {
            throw new InvalidOperationException(
                "Cannot edit a booked slot; cancel the reservation instead.");
        }

        if (request.Scope == OccurrenceEditScope.EntireRecurrence)
        {
            return await ApplyEntireRecurrenceAsync(tenantId, slot, request, cancellationToken);
        }

        return request.Action switch
        {
            DailyOccurrenceAction.Update =>
                await UpdateDailyOccurrenceAsync(tenantId, slot, request, cancellationToken),
            DailyOccurrenceAction.MakeUnavailable =>
                await MakeDailyOccurrenceUnavailableAsync(tenantId, slot, request, cancellationToken),
            DailyOccurrenceAction.RestoreWeeklyDefault =>
                await RestoreWeeklyDefaultAsync(slot, request, cancellationToken),
            _ => throw new ArgumentException("Unknown daily occurrence action."),
        };
    }

    private async Task<SlotResponseDto> ApplyEntireRecurrenceAsync(
        Guid tenantId,
        Slot? existing,
        ApplyDailyOccurrenceRequestDto request,
        CancellationToken cancellationToken)
    {
        var rentable = await dbContext.RentalAssets
            .Include(r => r.Asset)
            .FirstAsync(r => r.Id == request.RentalAssetId, cancellationToken);

        if (rentable.SchedulePolicy != SchedulePolicy.SlotGrid)
        {
            throw new InvalidOperationException(
                "Entire recurrence edits are only available for custom weekly grids. Change the weekly window under Weekly setup.");
        }

        if (request.Action == DailyOccurrenceAction.RestoreWeeklyDefault)
        {
            throw new InvalidOperationException(
                "Restore applies only to a single day override.");
        }

        var dayOfWeek = request.Date.DayOfWeek;
        var template = await dbContext.ScheduleTemplates
            .Include(t => t.OccupancyKind)
            .Include(t => t.RentalAsset).ThenInclude(r => r.Asset)
            .FirstOrDefaultAsync(
                t => t.RentalAssetId == request.RentalAssetId
                     && t.DayOfWeek == dayOfWeek
                     && t.StartTime == request.StartTime,
                cancellationToken);

        var previousKindId = template?.OccupancyKindId;
        var previousLabel = TrimLabel(template?.Label);
        var previousEnd = template?.EndTime ?? request.EndTime;
        var previousActive = template?.IsActive ?? true;

        if (request.Action == DailyOccurrenceAction.Update)
        {
            if (request.OccupancyKindId is not { } kindId)
            {
                throw new ArgumentException("OccupancyKindId is required to update a recurrence.");
            }

            await EnsureOccupancyKindAsync(kindId, cancellationToken);
            var label = TrimLabel(request.Label);

            if (template is null)
            {
                template = new ScheduleTemplate
                {
                    TenantId = tenantId,
                    RentalAssetId = request.RentalAssetId,
                    DayOfWeek = dayOfWeek,
                    StartTime = request.StartTime,
                    EndTime = request.EndTime,
                    OccupancyKindId = kindId,
                    Label = label,
                    IsActive = true,
                };
                dbContext.ScheduleTemplates.Add(template);
            }
            else
            {
                template.EndTime = request.EndTime;
                template.OccupancyKindId = kindId;
                template.Label = label;
                template.IsActive = true;
                template.Touch();
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            await CascadeTemplateToFutureSlotsAsync(
                template,
                request.Date,
                previousKindId,
                previousLabel,
                previousEnd,
                previousActive,
                makeUnavailable: false,
                cancellationToken);

            return await GetSlotDtoAsync(
                (await EnsurePublishedSlotFromTemplateAsync(
                    tenantId, template, request.Date, existing, cancellationToken)).Id,
                cancellationToken);
        }

        // MakeUnavailable for entire recurrence: deactivate template and cancel matching future slots.
        if (template is null)
        {
            throw new InvalidOperationException("There is no weekly rule to make unavailable.");
        }

        template.IsActive = false;
        template.Touch();
        await dbContext.SaveChangesAsync(cancellationToken);

        await CascadeTemplateToFutureSlotsAsync(
            template,
            request.Date,
            previousKindId,
            previousLabel,
            previousEnd,
            previousActive,
            makeUnavailable: true,
            cancellationToken);

        if (existing is not null && existing.Status != SlotStatus.Booked)
        {
            existing.MarkCancelled();
            await dbContext.SaveChangesAsync(cancellationToken);
            await dbContext.Entry(existing).Reference(s => s.OccupancyKind).LoadAsync(cancellationToken);
            if (existing.RentalAsset is null)
            {
                await dbContext.Entry(existing).Reference(s => s.RentalAsset).LoadAsync(cancellationToken);
            }
            if (existing.RentalAsset is not null)
            {
                await dbContext.Entry(existing.RentalAsset).Reference(r => r.Asset).LoadAsync(cancellationToken);
            }

            return ToSlotDto(existing, isDerived: false, SlotOccurrenceSource.DailyOverride);
        }

        return await MakeDailyOccurrenceUnavailableAsync(tenantId, existing, request, cancellationToken);
    }

    private async Task CascadeTemplateToFutureSlotsAsync(
        ScheduleTemplate template,
        DateOnly fromDate,
        Guid? previousKindId,
        string? previousLabel,
        TimeOnly previousEnd,
        bool previousActive,
        bool makeUnavailable,
        CancellationToken cancellationToken)
    {
        if (!previousActive && !makeUnavailable)
        {
            return;
        }

        var candidates = await dbContext.Slots
            .Include(s => s.OccupancyKind)
            .Include(s => s.RentalAsset).ThenInclude(r => r.Asset)
            .Where(s => s.RentalAssetId == template.RentalAssetId
                        && s.Date >= fromDate
                        && s.StartTime == template.StartTime
                        && s.Status != SlotStatus.Booked
                        && (s.SourceTemplateId == template.Id
                            || s.SourceTemplateId == null))
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            var matchesPrevious =
                (!previousKindId.HasValue || candidate.OccupancyKindId == previousKindId)
                && TrimLabel(candidate.Label) == previousLabel
                && candidate.EndTime == previousEnd;

            // Preserve intentional daily overrides that diverged from the previous template fingerprint.
            if (candidate.SourceTemplateId is null && !matchesPrevious)
            {
                continue;
            }

            if (candidate.SourceTemplateId == template.Id && !matchesPrevious && candidate.Status == SlotStatus.Cancelled)
            {
                // Keep unrelated cancelled overrides unless they still match the previous rule.
                continue;
            }

            if (!matchesPrevious && candidate.SourceTemplateId == template.Id)
            {
                // Diverged from previous template values → treat as daily override and skip.
                continue;
            }

            if (makeUnavailable)
            {
                candidate.MarkCancelled();
                candidate.SourceTemplateId = template.Id;
            }
            else
            {
                candidate.EndTime = template.EndTime;
                candidate.OccupancyKindId = template.OccupancyKindId;
                candidate.Label = template.Label;
                candidate.SourceTemplateId = template.Id;
                candidate.Status = SlotStatus.Available;
                candidate.Touch();
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<Slot> EnsurePublishedSlotFromTemplateAsync(
        Guid tenantId,
        ScheduleTemplate template,
        DateOnly date,
        Slot? existing,
        CancellationToken cancellationToken)
    {
        if (existing is not null)
        {
            existing.EndTime = template.EndTime;
            existing.OccupancyKindId = template.OccupancyKindId;
            existing.Label = template.Label;
            existing.SourceTemplateId = template.Id;
            existing.Status = SlotStatus.Available;
            existing.Touch();
            await dbContext.SaveChangesAsync(cancellationToken);
            return existing;
        }

        var created = new Slot
        {
            TenantId = tenantId,
            RentalAssetId = template.RentalAssetId,
            Date = date,
            StartTime = template.StartTime,
            EndTime = template.EndTime,
            OccupancyKindId = template.OccupancyKindId,
            Label = template.Label,
            Status = SlotStatus.Available,
            SourceTemplateId = template.Id,
        };
        dbContext.Slots.Add(created);
        await dbContext.SaveChangesAsync(cancellationToken);
        return created;
    }

    public async Task<ReservationResponseDto> BookSlotAsync(
        Guid customerId,
        BookSlotRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        await trialGuard.EnsureWritableAsync(cancellationToken);
        var quantity = request.Quantity < 1 ? 1 : request.Quantity;

        var customer = await dbContext.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken)
            ?? throw new UnauthorizedAccessException("Authenticated customer was not found.");

        if (customer.TenantId != tenantId)
        {
            throw new UnauthorizedAccessException("Customer does not belong to the current tenant.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var rentalAssetId = await dbContext.Slots
                .Where(s => s.Id == request.SlotId)
                .Select(s => s.RentalAssetId)
                .FirstOrDefaultAsync(cancellationToken);

            if (rentalAssetId == Guid.Empty)
            {
                throw new KeyNotFoundException("Slot was not found.");
            }

            await RentalAssetLocks.LockByRentalAssetIdAsync(
                dbContext,
                rentalAssetId,
                cancellationToken);

            var slot = await dbContext.Slots
                .Include(s => s.OccupancyKind)
                .Include(s => s.RentalAsset).ThenInclude(r => r.Asset)
                .FirstOrDefaultAsync(s => s.Id == request.SlotId, cancellationToken)
                ?? throw new KeyNotFoundException("Slot was not found.");

            if (slot.Status != SlotStatus.Available
                || !slot.OccupancyKind.IsBookableByCustomer
                || !slot.OccupancyKind.IsActive)
            {
                throw new InvalidOperationException("Slot is not available for booking.");
            }

            if (slot.RentalAsset.Asset.UnitId != request.UnitId)
            {
                throw new InvalidOperationException("Slot does not belong to the given unit.");
            }

            var start = ToDateTime(slot.Date, slot.StartTime);
            var end = ToDateTime(slot.Date, slot.EndTime);
            var hours = (decimal)(end - start).TotalHours;

            var reservedQuantity = await GetReservedQuantityAsync(
                slot.RentalAssetId, start, end, cancellationToken);

            if (slot.RentalAsset.Type == RentalAssetType.Location && reservedQuantity > 0)
            {
                throw new InvalidOperationException("Location is already reserved for this interval.");
            }

            if (slot.RentalAsset.Type == RentalAssetType.Good
                && quantity > slot.RentalAsset.TotalQuantity - reservedQuantity)
            {
                throw new InvalidOperationException("Insufficient quantity for this slot.");
            }

            var unitPrice = await ResolveHourlyPriceAsync(
                slot.RentalAssetId,
                slot.Date,
                slot.StartTime,
                slot.EndTime,
                cancellationToken);

            var subTotal = RoundMoney(unitPrice * hours * quantity);
            var whatsApp = string.IsNullOrWhiteSpace(customer.Phone) ? string.Empty : customer.Phone;

            var reservation = new Reservation
            {
                TenantId = tenantId,
                UnitId = request.UnitId,
                CustomerId = customer.Id,
                CustomerName = customer.Name,
                CustomerWhatsApp = whatsApp,
                StartDateTime = start,
                EndDateTime = end,
                Status = ReservationStatus.PendingDeposit,
                TotalAmount = subTotal,
                DepositPaid = 0m,
            };

            var item = new ReservationItem
            {
                TenantId = tenantId,
                ReservationId = reservation.Id,
                RentalAssetId = slot.RentalAssetId,
                Quantity = quantity,
                UnitPrice = unitPrice,
                SubTotal = subTotal,
            };
            reservation.AddItem(item);

            dbContext.Reservations.Add(reservation);
            reservation.OpenAccordingToPaymentPolicy(slot.RentalAsset.RequiresDeposit);
            slot.MarkBooked(reservation.Id);

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new ReservationResponseDto(
                reservation.Id,
                reservation.TenantId,
                reservation.UnitId,
                reservation.CustomerId,
                reservation.CustomerName,
                reservation.CustomerWhatsApp,
                reservation.StartDateTime,
                reservation.EndDateTime,
                reservation.Status,
                reservation.TotalAmount,
                reservation.DepositPaid,
                reservation.CreatedAt,
                [
                    new ReservationItemResponseDto(
                        item.Id,
                        slot.RentalAsset.AssetId,
                        slot.RentalAssetId,
                        slot.RentalAsset.Asset.Name,
                        item.Quantity,
                        item.UnitPrice,
                        item.SubTotal)
                ]);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<Slot?> ResolveOccurrenceSlotAsync(
        ApplyDailyOccurrenceRequestDto request,
        CancellationToken cancellationToken)
    {
        if (request.SlotId is { } slotId && slotId != Guid.Empty)
        {
            return await dbContext.Slots
                .Include(s => s.RentalAsset).ThenInclude(r => r.Asset)
                .Include(s => s.OccupancyKind)
                .FirstOrDefaultAsync(s => s.Id == slotId, cancellationToken)
                ?? throw new KeyNotFoundException("Slot was not found.");
        }

        return await dbContext.Slots
            .Include(s => s.RentalAsset).ThenInclude(r => r.Asset)
            .Include(s => s.OccupancyKind)
            .FirstOrDefaultAsync(
                s => s.RentalAssetId == request.RentalAssetId
                     && s.Date == request.Date
                     && s.StartTime == request.StartTime,
                cancellationToken);
    }

    private async Task<SlotResponseDto> UpdateDailyOccurrenceAsync(
        Guid tenantId,
        Slot? existing,
        ApplyDailyOccurrenceRequestDto request,
        CancellationToken cancellationToken)
    {
        if (request.OccupancyKindId is not { } kindId)
        {
            throw new ArgumentException("OccupancyKindId is required to update a daily occurrence.");
        }

        await EnsureOccupancyKindAsync(kindId, cancellationToken);
        var label = TrimLabel(request.Label);

        if (existing is not null)
        {
            existing.EndTime = request.EndTime;
            existing.OccupancyKindId = kindId;
            existing.Label = label;
            existing.Status = SlotStatus.Available;
            existing.Touch();
            await dbContext.SaveChangesAsync(cancellationToken);
            await dbContext.Entry(existing).Reference(s => s.OccupancyKind).LoadAsync(cancellationToken);
            return ToSlotDto(existing, isDerived: false, SlotOccurrenceSource.DailyOverride);
        }

        var created = new Slot
        {
            TenantId = tenantId,
            RentalAssetId = request.RentalAssetId,
            Date = request.Date,
            StartTime = request.StartTime,
            EndTime = request.EndTime,
            OccupancyKindId = kindId,
            Label = label,
            Status = SlotStatus.Available,
        };
        dbContext.Slots.Add(created);
        await dbContext.SaveChangesAsync(cancellationToken);
        return await GetSlotDtoAsync(created.Id, cancellationToken);
    }

    private async Task<SlotResponseDto> MakeDailyOccurrenceUnavailableAsync(
        Guid tenantId,
        Slot? existing,
        ApplyDailyOccurrenceRequestDto request,
        CancellationToken cancellationToken)
    {
        if (existing is not null)
        {
            existing.MarkCancelled();
            await dbContext.SaveChangesAsync(cancellationToken);
            await dbContext.Entry(existing).Reference(s => s.OccupancyKind).LoadAsync(cancellationToken);
            return ToSlotDto(existing, isDerived: false, SlotOccurrenceSource.DailyOverride);
        }

        var openKind = await dbContext.OccupancyKinds
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.Key == "open" && k.IsActive, cancellationToken)
            ?? await dbContext.OccupancyKinds
                .AsNoTracking()
                .FirstOrDefaultAsync(k => k.IsActive, cancellationToken)
            ?? throw new InvalidOperationException(
                "No occupancy kind is available for the unavailable marker.");

        var tombstone = new Slot
        {
            TenantId = tenantId,
            RentalAssetId = request.RentalAssetId,
            Date = request.Date,
            StartTime = request.StartTime,
            EndTime = request.EndTime,
            OccupancyKindId = openKind.Id,
            Label = null,
            Status = SlotStatus.Cancelled,
        };
        dbContext.Slots.Add(tombstone);
        await dbContext.SaveChangesAsync(cancellationToken);
        return await GetSlotDtoAsync(tombstone.Id, cancellationToken);
    }

    private async Task<SlotResponseDto> RestoreWeeklyDefaultAsync(
        Slot? existing,
        ApplyDailyOccurrenceRequestDto request,
        CancellationToken cancellationToken)
    {
        var rentable = await dbContext.RentalAssets
            .AsNoTracking()
            .Include(r => r.Asset)
            .FirstAsync(r => r.Id == request.RentalAssetId, cancellationToken);

        var template = await dbContext.ScheduleTemplates
            .AsNoTracking()
            .Include(t => t.OccupancyKind)
            .FirstOrDefaultAsync(
                t => t.RentalAssetId == request.RentalAssetId
                     && t.IsActive
                     && t.DayOfWeek == request.Date.DayOfWeek
                     && t.StartTime == request.StartTime,
                cancellationToken);

        if (rentable.SchedulePolicy == SchedulePolicy.OpenHours || template is null)
        {
            if (existing is not null)
            {
                dbContext.Slots.Remove(existing);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            if (rentable.SchedulePolicy != SchedulePolicy.OpenHours)
            {
                throw new InvalidOperationException(
                    "There is no weekly default to restore for this occurrence.");
            }

            var openKind = await dbContext.OccupancyKinds
                .AsNoTracking()
                .FirstOrDefaultAsync(k => k.Key == "open" && k.IsActive, cancellationToken)
                ?? throw new InvalidOperationException("Open occupancy kind was not found.");

            return new SlotResponseDto(
                Guid.Empty,
                rentable.Id,
                rentable.Asset.Name,
                request.Date,
                request.StartTime,
                request.EndTime,
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
                SupportsEntireRecurrence: false);
        }

        if (existing is null)
        {
            var created = new Slot
            {
                TenantId = EnsureTenant(),
                RentalAssetId = request.RentalAssetId,
                Date = request.Date,
                StartTime = template.StartTime,
                EndTime = template.EndTime,
                OccupancyKindId = template.OccupancyKindId,
                Label = template.Label,
                Status = SlotStatus.Available,
                SourceTemplateId = template.Id,
            };
            dbContext.Slots.Add(created);
            await dbContext.SaveChangesAsync(cancellationToken);
            return await GetSlotDtoAsync(created.Id, cancellationToken);
        }

        existing.EndTime = template.EndTime;
        existing.OccupancyKindId = template.OccupancyKindId;
        existing.Label = template.Label;
        existing.SourceTemplateId = template.Id;
        existing.MarkAvailable();
        await dbContext.SaveChangesAsync(cancellationToken);
        await dbContext.Entry(existing).Reference(s => s.OccupancyKind).LoadAsync(cancellationToken);
        return ToSlotDto(existing, isDerived: false, SlotOccurrenceSource.WeeklyDefault);
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
                    t.RentalAssetId == slot.RentalAssetId && t.StartTime == slot.StartTime);
                if (match is not null)
                {
                    map[slot.Id] = match;
                }
            }
        }

        return map;
    }

    private static SlotOccurrenceSource ResolvePersistedSource(Slot slot, ScheduleTemplate? template)
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
            date, rentables.Select(r => r.Id).ToList(), cancellationToken);

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
                    if (end > close)
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
    /// OpenHours derives from open/close. Persisted starts (including cancelled tombstones) win.
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
            date,
            templates.Select(t => t.RentalAssetId).Distinct().ToList(),
            cancellationToken);

        var derived = new List<SlotResponseDto>();
        foreach (var template in templates)
        {
            if (persistedStarts.Contains((template.RentalAssetId, template.StartTime)))
            {
                continue;
            }

            var startDt = ToDateTime(date, template.StartTime);
            var endDt = ToDateTime(date, template.EndTime);
            var windows = reservedWindows.TryGetValue(template.RentalAssetId, out var found)
                ? found
                : [];
            var reserved = SumOverlapping(windows, startDt, endDt);
            var available = template.RentalAsset.Type == RentalAssetType.Location
                ? reserved == 0
                : reserved < template.RentalAsset.TotalQuantity;

            if (!available)
            {
                continue;
            }

            derived.Add(new SlotResponseDto(
                Guid.Empty,
                template.RentalAssetId,
                template.RentalAsset.Asset.Name,
                date,
                template.StartTime,
                template.EndTime,
                template.OccupancyKindId,
                template.OccupancyKind.Key,
                template.OccupancyKind.Label,
                template.OccupancyKind.ColorHex,
                template.OccupancyKind.IsBookableByCustomer,
                template.Label,
                SlotStatus.Available,
                null,
                IsDerived: true,
                SlotOccurrenceSource.WeeklyDefault,
                SourceTemplateId: template.Id,
                SchedulePolicy.SlotGrid,
                SupportsEntireRecurrence: true));
        }

        return derived;
    }

    private async Task<bool> EnsureSlotGridPolicyAsync(
        IReadOnlyList<Guid> rentableIds,
        CancellationToken cancellationToken)
    {
        var rentables = await dbContext.RentalAssets
            .Where(r => rentableIds.Contains(r.Id))
            .ToListAsync(cancellationToken);

        var changed = false;
        foreach (var rentable in rentables)
        {
            if (rentable.SchedulePolicy == SchedulePolicy.SlotGrid)
            {
                continue;
            }

            rentable.SchedulePolicy = SchedulePolicy.SlotGrid;
            rentable.Touch();
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Loads every blocking reservation window that touches <paramref name="date"/> in a single
    /// query, so slot derivation can be computed in memory instead of one round trip per slot.
    /// </summary>
    private async Task<Dictionary<Guid, List<ReservedWindow>>> LoadReservedWindowsAsync(
        DateOnly date,
        IReadOnlyList<Guid> rentalAssetIds,
        CancellationToken cancellationToken)
    {
        if (rentalAssetIds.Count == 0)
        {
            return [];
        }

        var dayStart = ToDateTime(date, TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);

        var rows = await (
            from item in dbContext.ReservationItems.AsNoTracking()
            join reservation in dbContext.Reservations.AsNoTracking()
                on item.ReservationId equals reservation.Id
            where rentalAssetIds.Contains(item.RentalAssetId)
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

    internal async Task<int> GetReservedQuantityAsync(
        Guid rentalAssetId,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var query =
            from item in dbContext.ReservationItems.AsNoTracking()
            join reservation in dbContext.Reservations.AsNoTracking()
                on item.ReservationId equals reservation.Id
            where item.RentalAssetId == rentalAssetId
                  && BlockingStatuses.Contains(reservation.Status)
                  && reservation.StartDateTime < end
                  && reservation.EndDateTime > start
            select item.Quantity;

        return await query.SumAsync(cancellationToken);
    }

    private async Task<decimal> ResolveHourlyPriceAsync(
        Guid rentalAssetId,
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        CancellationToken cancellationToken)
    {
        var dayOfWeek = date.DayOfWeek;
        var pricing = await dbContext.RentalPricings
            .AsNoTracking()
            .Where(p => p.RentalAssetId == rentalAssetId
                        && p.DayOfWeek == dayOfWeek
                        && p.StartTime <= startTime
                        && p.EndTime >= endTime)
            .OrderByDescending(p => p.StartTime)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "No pricing covers the requested interval for this rentable.");

        return pricing.PricePerHour;
    }

    private async Task EnsureRentableAsync(Guid rentalAssetId, CancellationToken cancellationToken)
    {
        var exists = await dbContext.RentalAssets
            .AnyAsync(r => r.Id == rentalAssetId && r.IsActive, cancellationToken);
        if (!exists)
        {
            throw new KeyNotFoundException("Rentable was not found.");
        }
    }

    private async Task EnsureRentablesAsync(
        IReadOnlyList<Guid> rentalAssetIds,
        CancellationToken cancellationToken)
    {
        if (rentalAssetIds.Count == 0)
        {
            throw new ArgumentException("At least one rentable is required.");
        }

        var found = await dbContext.RentalAssets
            .Where(r => rentalAssetIds.Contains(r.Id) && r.IsActive)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

        if (found.Count != rentalAssetIds.Count)
        {
            throw new KeyNotFoundException("One or more rentables were not found.");
        }
    }

    private static IReadOnlyList<Guid> ResolveRentableIds(
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

    private async Task EnsureOccupancyKindAsync(Guid occupancyKindId, CancellationToken cancellationToken)
    {
        var exists = await dbContext.OccupancyKinds
            .AnyAsync(k => k.Id == occupancyKindId && k.IsActive, cancellationToken);
        if (!exists)
        {
            throw new KeyNotFoundException("Occupancy kind was not found.");
        }
    }

    private async Task<ScheduleTemplateResponseDto> GetTemplateDtoAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.ScheduleTemplates
            .AsNoTracking()
            .Include(t => t.RentalAsset).ThenInclude(r => r.Asset)
            .Include(t => t.OccupancyKind)
            .FirstAsync(t => t.Id == id, cancellationToken);
        return ToTemplateDto(entity);
    }

    private async Task<SlotResponseDto> GetSlotDtoAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await dbContext.Slots
            .AsNoTracking()
            .Include(s => s.RentalAsset).ThenInclude(r => r.Asset)
            .Include(s => s.OccupancyKind)
            .FirstAsync(s => s.Id == id, cancellationToken);

        ScheduleTemplate? template = null;
        if (entity.SourceTemplateId is { } tid)
        {
            template = await dbContext.ScheduleTemplates
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == tid, cancellationToken);
        }
        else
        {
            template = await dbContext.ScheduleTemplates
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    t => t.RentalAssetId == entity.RentalAssetId
                         && t.IsActive
                         && t.DayOfWeek == entity.Date.DayOfWeek
                         && t.StartTime == entity.StartTime,
                    cancellationToken);
        }

        return ToSlotDto(entity, isDerived: false, ResolvePersistedSource(entity, template));
    }

    private Guid EnsureTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");

    private static void ValidateTimeRange(TimeOnly start, TimeOnly end)
    {
        if (end <= start)
        {
            throw new ArgumentException("End time must be after start time.");
        }
    }

    private static string? TrimLabel(string? label) =>
        string.IsNullOrWhiteSpace(label) ? null : label.Trim();

    private static DateTimeOffset ToDateTime(DateOnly date, TimeOnly time) =>
        new(date.ToDateTime(time), TimeSpan.Zero);

    private static decimal RoundMoney(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static ScheduleTemplateResponseDto ToTemplateDto(ScheduleTemplate t) =>
        new(
            t.Id,
            t.RentalAssetId,
            t.RentalAsset.Asset.Name,
            t.DayOfWeek,
            t.StartTime,
            t.EndTime,
            t.OccupancyKindId,
            t.OccupancyKind.Label,
            t.Label,
            t.IsActive);

    private static SlotResponseDto ToSlotDto(
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
