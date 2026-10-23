using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using static Platform.Api.Modules.Rentals.Services.AvailabilityReadModel;
using static Platform.Api.Modules.Rentals.Services.ScheduleWriteGuards;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>
/// Day writes: publish a day from templates, upsert/cancel a day's slot and apply
/// daily occurrence overrides (including entire-recurrence edits).
/// </summary>
internal sealed class DayPublisher(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IOccupancyKindService occupancyKindService)
{
    private readonly ScheduleWriteGuards guards = new(dbContext, tenantProvider);

    public async Task<int> PublishDayAsync(
        PublishDayRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = guards.EnsureTenant();
        await occupancyKindService.EnsureDefaultsAsync(cancellationToken);

        var dayOfWeek = request.Date.DayOfWeek;
        var ids = ResolveRentableIds(request.RentalAssetId, request.RentalAssetIds);
        if (ids.Count == 0)
        {
            ids = await dbContext.RentalAssets
                .Where(r => r.TenantId == tenantId
                            && r.IsActive
                            && r.SchedulePolicy == SchedulePolicy.SlotGrid)
                .Select(r => r.Id)
                .ToListAsync(cancellationToken);
        }

        await using var transaction = await guards.BeginAssetLockedTransactionAsync(
            tenantId, ids, cancellationToken);
        try
        {
            var templatesQuery = dbContext.ScheduleTemplates
                .Include(t => t.RentalAsset)
                .Where(t => t.TenantId == tenantId && t.IsActive && t.DayOfWeek == dayOfWeek);

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
                    .Where(s => s.TenantId == tenantId
                                && s.Date == request.Date
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

            await CommitIfPresentAsync(transaction, cancellationToken);
            return created;
        }
        catch
        {
            await RollbackIfPresentAsync(transaction, cancellationToken);
            throw;
        }
    }

    public async Task<SlotResponseDto> UpsertSlotAsync(
        UpsertSlotRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = guards.EnsureTenant();
        ValidateTimeRange(request.StartTime, request.EndTime);
        await guards.EnsureRentableAsync(tenantId, request.RentalAssetId, cancellationToken);
        await guards.EnsureOccupancyKindAsync(tenantId, request.OccupancyKindId, cancellationToken);

        await using var transaction = await guards.BeginAssetLockedTransactionAsync(
            tenantId, [request.RentalAssetId], cancellationToken);
        try
        {
            var existing = await dbContext.Slots
                .Include(s => s.RentalAsset).ThenInclude(r => r.Asset)
                .Include(s => s.OccupancyKind)
                .FirstOrDefaultAsync(
                    s => s.TenantId == tenantId
                         && s.RentalAssetId == request.RentalAssetId
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
                await CommitIfPresentAsync(transaction, cancellationToken);

                await dbContext.Entry(existing).Reference(s => s.OccupancyKind).LoadAsync(cancellationToken);
                var template = existing.SourceTemplateId is { } tid
                    ? await dbContext.ScheduleTemplates.AsNoTracking()
                        .FirstOrDefaultAsync(t => t.Id == tid && t.TenantId == tenantId, cancellationToken)
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
            await CommitIfPresentAsync(transaction, cancellationToken);

            return await GetSlotDtoAsync(slot.Id, cancellationToken);
        }
        catch
        {
            await RollbackIfPresentAsync(transaction, cancellationToken);
            throw;
        }
    }

    public async Task CancelSlotAsync(Guid slotId, CancellationToken cancellationToken)
    {
        var tenantId = guards.EnsureTenant();
        var existing = await dbContext.Slots
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == slotId && s.TenantId == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Slot was not found.");

        await using var transaction = await guards.BeginAssetLockedTransactionAsync(
            tenantId, [existing.RentalAssetId], cancellationToken);
        try
        {
            var slot = await dbContext.Slots
                .FirstOrDefaultAsync(s => s.Id == slotId && s.TenantId == tenantId, cancellationToken)
                ?? throw new KeyNotFoundException("Slot was not found.");

            if (slot.Status == SlotStatus.Booked)
            {
                throw new InvalidOperationException("Cannot cancel a booked slot; cancel the reservation instead.");
            }

            slot.MarkCancelled();
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitIfPresentAsync(transaction, cancellationToken);
        }
        catch
        {
            await RollbackIfPresentAsync(transaction, cancellationToken);
            throw;
        }
    }

    public async Task<SlotResponseDto> ApplyDailyOccurrenceAsync(
        ApplyDailyOccurrenceRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = guards.EnsureTenant();
        ValidateTimeRange(request.StartTime, request.EndTime);
        await guards.EnsureRentableAsync(tenantId, request.RentalAssetId, cancellationToken);

        await using var transaction = await guards.BeginAssetLockedTransactionAsync(
            tenantId, [request.RentalAssetId], cancellationToken);
        try
        {
            var slot = await ResolveOccurrenceSlotAsync(tenantId, request, cancellationToken);

            if (slot is not null && slot.Status == SlotStatus.Booked)
            {
                throw new InvalidOperationException(
                    "Cannot edit a booked slot; cancel the reservation instead.");
            }

            SlotResponseDto result;
            if (request.Scope == OccurrenceEditScope.EntireRecurrence)
            {
                result = await ApplyEntireRecurrenceAsync(tenantId, slot, request, cancellationToken);
            }
            else
            {
                result = request.Action switch
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

            await CommitIfPresentAsync(transaction, cancellationToken);
            return result;
        }
        catch
        {
            await RollbackIfPresentAsync(transaction, cancellationToken);
            throw;
        }
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
        var currentKindId = existing?.OccupancyKindId ?? request.OccupancyKindId;
        var currentEnd = existing?.EndTime ?? request.EndTime;
        var currentStart = existing?.StartTime ?? request.StartTime;
        var template = await ResolveTemplateForOccurrenceAsync(
            existing,
            request.RentalAssetId,
            dayOfWeek,
            currentStart,
            currentEnd,
            currentKindId,
            activeOnly: false,
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

            await guards.EnsureOccupancyKindAsync(tenantId, kindId, cancellationToken);
            var label = TrimLabel(request.Label);

            if (template is null)
            {
                await guards.EnsureNoTemplateCollisionAsync(
                    tenantId,
                    request.RentalAssetId,
                    dayOfWeek,
                    request.StartTime,
                    request.EndTime,
                    kindId,
                    excludeId: null,
                    cancellationToken);

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
                await guards.EnsureNoTemplateCollisionAsync(
                    tenantId,
                    template.RentalAssetId,
                    template.DayOfWeek,
                    request.StartTime,
                    request.EndTime,
                    kindId,
                    excludeId: template.Id,
                    cancellationToken);

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

    private async Task<Slot?> ResolveOccurrenceSlotAsync(
        Guid tenantId,
        ApplyDailyOccurrenceRequestDto request,
        CancellationToken cancellationToken)
    {
        if (request.SlotId is { } slotId && slotId != Guid.Empty)
        {
            return await dbContext.Slots
                .Include(s => s.RentalAsset).ThenInclude(r => r.Asset)
                .Include(s => s.OccupancyKind)
                .FirstOrDefaultAsync(
                    s => s.Id == slotId
                         && s.TenantId == tenantId
                         && s.RentalAssetId == request.RentalAssetId
                         && s.Date == request.Date
                         && s.StartTime == request.StartTime,
                    cancellationToken)
                ?? throw new KeyNotFoundException("Slot was not found.");
        }

        return await dbContext.Slots
            .Include(s => s.RentalAsset).ThenInclude(r => r.Asset)
            .Include(s => s.OccupancyKind)
            .FirstOrDefaultAsync(
                s => s.TenantId == tenantId
                     && s.RentalAssetId == request.RentalAssetId
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

        await guards.EnsureOccupancyKindAsync(tenantId, kindId, cancellationToken);
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

        var template = await ResolveTemplateForOccurrenceAsync(
            existing,
            request.RentalAssetId,
            request.Date.DayOfWeek,
            existing?.StartTime ?? request.StartTime,
            existing?.EndTime ?? request.EndTime,
            existing?.OccupancyKindId ?? request.OccupancyKindId,
            activeOnly: true,
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
                TenantId = guards.EnsureTenant(),
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

    private async Task<ScheduleTemplate?> ResolveTemplateForOccurrenceAsync(
        Slot? existing,
        Guid rentalAssetId,
        DayOfWeek dayOfWeek,
        TimeOnly startTime,
        TimeOnly endTime,
        Guid? occupancyKindId,
        bool activeOnly,
        CancellationToken cancellationToken)
    {
        if (existing?.SourceTemplateId is { } sourceId)
        {
            var bySource = await TrackedTemplateQuery()
                .FirstOrDefaultAsync(t => t.Id == sourceId, cancellationToken);
            if (bySource is not null && (!activeOnly || bySource.IsActive))
            {
                return bySource;
            }
        }

        if (occupancyKindId is not { } kindId)
        {
            return null;
        }

        var query = TrackedTemplateQuery()
            .Where(t => t.RentalAssetId == rentalAssetId
                        && t.DayOfWeek == dayOfWeek
                        && t.StartTime == startTime
                        && t.EndTime == endTime
                        && t.OccupancyKindId == kindId);
        if (activeOnly)
        {
            query = query.Where(t => t.IsActive);
        }

        return await query.OrderBy(t => t.Id).FirstOrDefaultAsync(cancellationToken);

        IQueryable<ScheduleTemplate> TrackedTemplateQuery() =>
            dbContext.ScheduleTemplates
                .Include(t => t.OccupancyKind)
                .Include(t => t.RentalAsset).ThenInclude(r => r.Asset);
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
                         && t.StartTime == entity.StartTime
                         && t.EndTime == entity.EndTime
                         && t.OccupancyKindId == entity.OccupancyKindId,
                    cancellationToken);
        }

        return ToSlotDto(entity, isDerived: false, ResolvePersistedSource(entity, template));
    }
}
