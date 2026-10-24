using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Services.Trial;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

public sealed class TeacherLessonService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    ITrialGuard trialGuard,
    TimeProvider timeProvider,
    ITeacherLessonScope teacherLessonScope) : ITeacherLessonService
{
    private static readonly ReservationStatus[] BlockingStatuses = AvailabilityReadModel.BlockingStatuses;

    public async Task<SlotResponseDto> CreateAsync(
        CreateTeacherLessonRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        EnsurePilotScope(tenantId, request.RentalAssetId);
        await EnsureActiveTenantAsync(tenantId, cancellationToken);
        await trialGuard.EnsureWritableAsync(cancellationToken);
        ValidateTimeRange(request.StartTime, request.EndTime);
        await EnsureStartNotInPastAsync(
            tenantId, request.Date, request.StartTime, cancellationToken);
        var (openKind, lessonKind) = await LoadCanonicalKindsAsync(tenantId, cancellationToken);
        var label = TrimLabel(request.Label);

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            await RentalAssetLocks.LockByRentalAssetIdAsync(
                dbContext, tenantId, request.RentalAssetId, cancellationToken);

            var location = await LoadSlotGridLocationAsync(
                tenantId, request.RentalAssetId, cancellationToken);

            var overlappingSlots = await LoadOverlappingSlotsAsync(
                tenantId,
                request.RentalAssetId,
                request.Date,
                request.StartTime,
                request.EndTime,
                excludeSlotId: null,
                cancellationToken);

            var winning = await ResolveWinningOpenWindowAsync(
                tenantId,
                location,
                request.Date,
                request.StartTime,
                request.EndTime,
                openKind,
                cancellationToken);

            await EnsureNoBlockingReservationAsync(
                tenantId,
                request.RentalAssetId,
                request.Date,
                request.StartTime,
                request.EndTime,
                cancellationToken);

            await EnsureNoActiveNonOpenTemplateOverlapAsync(
                tenantId,
                request.RentalAssetId,
                request.Date.DayOfWeek,
                request.StartTime,
                request.EndTime,
                openKind.Id,
                cancellationToken);

            var exactDuplicate = overlappingSlots.FirstOrDefault(slot =>
                slot.StartTime == request.StartTime
                && slot.EndTime == request.EndTime
                && slot.OccupancyKindId == lessonKind.Id
                && slot.OccupancyKind.IsActive
                && string.Equals(slot.OccupancyKind.Key, "lesson", StringComparison.Ordinal)
                && TrimLabel(slot.Label) == label
                && slot.Status == SlotStatus.Available
                && slot.ReservationId is null
                && slot.SourceTemplateId == winning.Template.Id);

            if (exactDuplicate is not null)
            {
                if (overlappingSlots.Count != 1)
                {
                    throw new InvalidOperationException(
                        TeacherLessonMessages.OverlapsExistingSlot);
                }

                await CommitIfPresentAsync(transaction, cancellationToken);
                return ToSlotDto(exactDuplicate);
            }

            if (overlappingSlots.Count == 1)
            {
                var existing = overlappingSlots[0];
                if (existing.StartTime != request.StartTime
                    || existing.EndTime != request.EndTime
                    || existing.OccupancyKindId != openKind.Id
                    || !existing.OccupancyKind.IsActive
                    || existing.Status != SlotStatus.Available
                    || existing.ReservationId is not null
                    || existing.SourceTemplateId != winning.Template.Id)
                {
                    throw new InvalidOperationException(
                        TeacherLessonMessages.OverlapsNonOpenSlot);
                }

                existing.OccupancyKindId = lessonKind.Id;
                existing.OccupancyKind = lessonKind;
                existing.Label = label;
                existing.Touch();
                await dbContext.SaveChangesAsync(cancellationToken);
                await CommitIfPresentAsync(transaction, cancellationToken);
                existing.RentalAsset = location;
                return ToSlotDto(existing);
            }

            if (overlappingSlots.Count > 1)
            {
                throw new InvalidOperationException(TeacherLessonMessages.OverlapsExistingSlots);
            }

            var created = new Slot
            {
                TenantId = tenantId,
                RentalAssetId = location.Id,
                Date = request.Date,
                StartTime = request.StartTime,
                EndTime = request.EndTime,
                OccupancyKindId = lessonKind.Id,
                Label = label,
                Status = SlotStatus.Available,
                SourceTemplateId = winning.Template.Id,
            };
            dbContext.Slots.Add(created);
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitIfPresentAsync(transaction, cancellationToken);

            created.OccupancyKind = lessonKind;
            created.RentalAsset = location;
            return ToSlotDto(created);
        }
        catch (DbUpdateException exception) when (IsSlotStartUniqueViolation(exception))
        {
            await RollbackIfPresentAsync(transaction, cancellationToken);
            throw new InvalidOperationException(TeacherLessonMessages.SlotStartTaken, exception);
        }
        catch
        {
            await RollbackIfPresentAsync(transaction, cancellationToken);
            throw;
        }
    }

    public async Task<SlotResponseDto> RemoveAsync(
        RemoveTeacherLessonRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        EnsurePilotScope(tenantId, request.RentalAssetId);
        await EnsureActiveTenantAsync(tenantId, cancellationToken);
        await trialGuard.EnsureWritableAsync(cancellationToken);
        var (openKind, lessonKind) = await LoadCanonicalKindsAsync(tenantId, cancellationToken);

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            await RentalAssetLocks.LockByRentalAssetIdAsync(
                dbContext, tenantId, request.RentalAssetId, cancellationToken);

            var location = await LoadSlotGridLocationAsync(
                tenantId, request.RentalAssetId, cancellationToken);

            ValidateTimeRange(request.StartTime, request.EndTime);
            var templates = await LoadActiveTemplatesAsync(
                tenantId, request.RentalAssetId, request.Date.DayOfWeek, cancellationToken);
            var winning = ResolveExactWinningWindow(
                templates, request.StartTime, request.EndTime);

            var overlappingSlots = await LoadOverlappingSlotsAsync(
                tenantId,
                request.RentalAssetId,
                request.Date,
                request.StartTime,
                request.EndTime,
                excludeSlotId: null,
                cancellationToken);
            if (overlappingSlots.Count > 1
                || overlappingSlots.Any(slot =>
                    slot.StartTime != request.StartTime || slot.EndTime != request.EndTime))
            {
                throw new InvalidOperationException(
                    TeacherLessonMessages.OverlapsExistingSlot);
            }

            var source = winning.Template;
            Slot? target = overlappingSlots.SingleOrDefault();
            if (target is null)
            {
                if (source.OccupancyKindId != lessonKind.Id
                    || !source.OccupancyKind.IsActive
                    || !string.Equals(source.OccupancyKind.Key, "lesson", StringComparison.Ordinal))
                {
                    throw new KeyNotFoundException(TeacherLessonMessages.WeeklyLessonNotFound);
                }
            }
            else if (target.OccupancyKindId == lessonKind.Id
                     && target.OccupancyKind.IsActive
                     && string.Equals(target.OccupancyKind.Key, "lesson", StringComparison.Ordinal)
                     && target.Status == SlotStatus.Available
                     && target.ReservationId is null
                     && target.SourceTemplateId is not null)
            {
                var origin = await dbContext.ScheduleTemplates
                    .AsNoTracking()
                    .Include(t => t.OccupancyKind)
                    .FirstOrDefaultAsync(t => t.Id == target.SourceTemplateId
                                              && t.TenantId == tenantId,
                        cancellationToken);
                var winningIsCanonicalOpen = source.OccupancyKindId == openKind.Id
                                              && source.OccupancyKind.IsActive
                                              && string.Equals(
                                                  source.OccupancyKind.Key,
                                                  "open",
                                                  StringComparison.Ordinal);
                var sourceIsCanonicalLesson = origin is not null
                                               && origin.OccupancyKindId == lessonKind.Id
                                               && origin.OccupancyKind.IsActive
                                               && string.Equals(
                                                   origin.OccupancyKind.Key,
                                                   "lesson",
                                                   StringComparison.Ordinal);
                var sourceIsWinningOpen = origin is not null
                                           && winningIsCanonicalOpen
                                           && origin.Id == source.Id
                                           && origin.OccupancyKindId == openKind.Id
                                           && origin.OccupancyKind.IsActive
                                           && string.Equals(
                                               origin.OccupancyKind.Key,
                                               "open",
                                               StringComparison.Ordinal);
                if (origin is null
                    || origin.RentalAssetId != request.RentalAssetId
                    || origin.DayOfWeek != request.Date.DayOfWeek
                    || origin.StartTime > target.StartTime
                    || origin.EndTime < target.EndTime
                    || !origin.IsActive
                    || !origin.OccupancyKind.IsActive
                    || (!sourceIsCanonicalLesson && !sourceIsWinningOpen))
                {
                    throw new InvalidOperationException(TeacherLessonMessages.LessonSourceUnverifiable);
                }

                if (source.OccupancyKindId == lessonKind.Id
                    && target.SourceTemplateId != source.Id)
                {
                    throw new InvalidOperationException(
                        TeacherLessonMessages.DifferentWeeklyLessonWins);
                }
            }
            else if (target.OccupancyKindId == openKind.Id
                     && target.OccupancyKind.IsActive
                     && target.Status == SlotStatus.Available
                     && target.ReservationId is null
                     && target.SourceTemplateId == source.Id
                     && source.OccupancyKindId == lessonKind.Id)
            {
                await EnsureNoActiveNonOpenTemplateOverlapAsync(
                    tenantId,
                    request.RentalAssetId,
                    request.Date.DayOfWeek,
                    request.StartTime,
                    request.EndTime,
                    openKind.Id,
                    cancellationToken,
                    excludeTemplateId: source.Id);
                await EnsureNoBlockingReservationAsync(
                    tenantId,
                    request.RentalAssetId,
                    request.Date,
                    request.StartTime,
                    request.EndTime,
                    cancellationToken);
                await CommitIfPresentAsync(transaction, cancellationToken);
                target.RentalAsset = location;
                return ToSlotDto(target);
            }
            else
            {
                throw new InvalidOperationException(TeacherLessonMessages.OnlyExactLesson);
            }

            await EnsureNoActiveNonOpenTemplateOverlapAsync(
                tenantId,
                request.RentalAssetId,
                request.Date.DayOfWeek,
                request.StartTime,
                request.EndTime,
                openKind.Id,
                cancellationToken,
                excludeTemplateId: source.OccupancyKindId == lessonKind.Id
                                   && (target is null || target.SourceTemplateId == source.Id)
                    ? source.Id
                    : null);

            await EnsureNoBlockingReservationAsync(
                tenantId,
                request.RentalAssetId,
                request.Date,
                request.StartTime,
                request.EndTime,
                cancellationToken);

            if (target is null)
            {
                target = new Slot
                {
                    TenantId = tenantId,
                    RentalAssetId = request.RentalAssetId,
                    Date = request.Date,
                    StartTime = request.StartTime,
                    EndTime = request.EndTime,
                    OccupancyKindId = openKind.Id,
                    Label = null,
                    Status = SlotStatus.Available,
                    SourceTemplateId = source.Id,
                    OccupancyKind = openKind,
                    RentalAsset = location,
                };
                dbContext.Slots.Add(target);
            }
            else
            {
                target.OccupancyKindId = openKind.Id;
                target.OccupancyKind = openKind;
                target.Label = null;
                target.Status = SlotStatus.Available;
                target.ReservationId = null;
                target.Touch();
            }
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitIfPresentAsync(transaction, cancellationToken);

            target.RentalAsset = location;
            return ToSlotDto(target);
        }
        catch (DbUpdateException exception) when (IsSlotStartUniqueViolation(exception))
        {
            await RollbackIfPresentAsync(transaction, cancellationToken);
            throw new InvalidOperationException(TeacherLessonMessages.SlotStartTaken, exception);
        }
        catch
        {
            await RollbackIfPresentAsync(transaction, cancellationToken);
            throw;
        }
    }

    private async Task<OccupancyPrecedence.WinningWindow> ResolveWinningOpenWindowAsync(
        Guid tenantId,
        RentalAsset location,
        DateOnly date,
        TimeOnly start,
        TimeOnly end,
        OccupancyKind openKind,
        CancellationToken cancellationToken)
    {
        var templates = await LoadActiveTemplatesAsync(
            tenantId, location.Id, date.DayOfWeek, cancellationToken);

        var winning = ResolveExactWinningWindow(templates, start, end);
        if (winning.Template.OccupancyKindId != openKind.Id
            || !string.Equals(winning.Template.OccupancyKind.Key, "open", StringComparison.Ordinal)
            || !winning.Template.OccupancyKind.IsActive)
        {
            throw new InvalidOperationException(
                TeacherLessonMessages.NotOpenWindow);
        }

        return winning;
    }

    private async Task<List<ScheduleTemplate>> LoadActiveTemplatesAsync(
        Guid tenantId,
        Guid rentalAssetId,
        DayOfWeek dayOfWeek,
        CancellationToken cancellationToken) =>
        await dbContext.ScheduleTemplates
            .Include(t => t.OccupancyKind)
            .Where(t => t.TenantId == tenantId
                        && t.RentalAssetId == rentalAssetId
                        && t.IsActive
                        && t.DayOfWeek == dayOfWeek)
            .ToListAsync(cancellationToken);

    private static OccupancyPrecedence.WinningWindow ResolveExactWinningWindow(
        IReadOnlyList<ScheduleTemplate> templates,
        TimeOnly start,
        TimeOnly end)
    {
        var windows = OccupancyPrecedence.SplitWinningWindows(templates);
        var exact = windows
            .Where(window => window.Start == start && window.End == end)
            .ToList();

        if (exact.Count != 1)
        {
            throw new InvalidOperationException(
                TeacherLessonMessages.NotUniqueWindow);
        }

        return exact[0];
    }

    private async Task EnsureNoActiveNonOpenTemplateOverlapAsync(
        Guid tenantId,
        Guid rentalAssetId,
        DayOfWeek dayOfWeek,
        TimeOnly start,
        TimeOnly end,
        Guid openKindId,
        CancellationToken cancellationToken,
        Guid? excludeTemplateId = null)
    {
        var overlapping = await dbContext.ScheduleTemplates
            .AsNoTracking()
            .Include(t => t.OccupancyKind)
            .Where(t => t.TenantId == tenantId
                        && t.RentalAssetId == rentalAssetId
                        && t.IsActive
                        && t.DayOfWeek == dayOfWeek
                        && t.OccupancyKindId != openKindId
                        && (excludeTemplateId == null || t.Id != excludeTemplateId)
                        && t.StartTime < end
                        && t.EndTime > start)
            .ToListAsync(cancellationToken);

        if (overlapping.Count > 0)
        {
            throw new InvalidOperationException(
                TeacherLessonMessages.OverlapsNonOpenTemplate);
        }
    }

    private async Task EnsureNoBlockingReservationAsync(
        Guid tenantId,
        Guid rentalAssetId,
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        CancellationToken cancellationToken)
    {
        var zone = await TenantBookingClock.ResolveForTenantAsync(dbContext, tenantId, cancellationToken);
        var start = TenantBookingClock.AtLocal(date, startTime, zone);
        var end = TenantBookingClock.AtLocal(date, endTime, zone);

        var blocking = await (
            from item in dbContext.ReservationItems.AsNoTracking()
            join reservation in dbContext.Reservations.AsNoTracking()
                on item.ReservationId equals reservation.Id
            where item.TenantId == tenantId
                  && reservation.TenantId == tenantId
                  && item.RentalAssetId == rentalAssetId
                  && BlockingStatuses.Contains(reservation.Status)
                  && reservation.StartDateTime < end
                  && reservation.EndDateTime > start
            select item.Id).AnyAsync(cancellationToken);

        if (blocking)
        {
            throw new InvalidOperationException(
                TeacherLessonMessages.OverlapsReservation);
        }
    }

    private async Task<List<Slot>> LoadOverlappingSlotsAsync(
        Guid tenantId,
        Guid rentalAssetId,
        DateOnly date,
        TimeOnly start,
        TimeOnly end,
        Guid? excludeSlotId,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Slots
            .Include(s => s.OccupancyKind)
            .Include(s => s.RentalAsset).ThenInclude(r => r.Asset)
            .Where(s => s.TenantId == tenantId
                        && s.RentalAssetId == rentalAssetId
                        && s.Date == date
                        && s.StartTime < end
                        && s.EndTime > start);

        if (excludeSlotId is { } excluded)
        {
            query = query.Where(s => s.Id != excluded);
        }

        return await query.ToListAsync(cancellationToken);
    }

    private async Task<RentalAsset> LoadSlotGridLocationAsync(
        Guid tenantId,
        Guid rentalAssetId,
        CancellationToken cancellationToken)
    {
        var rental = await dbContext.RentalAssets
            .Include(r => r.Asset)
            .FirstOrDefaultAsync(
                r => r.Id == rentalAssetId
                     && r.TenantId == tenantId
                     && r.IsActive
                     && r.Type == RentalAssetType.Location
                     && r.Asset.IsRentable
                     && r.Asset.Status != AssetStatus.Inactive,
                cancellationToken)
            ?? throw new KeyNotFoundException(TeacherLessonMessages.RentableNotFound);

        if (rental.SchedulePolicy != SchedulePolicy.SlotGrid)
        {
            throw new InvalidOperationException(TeacherLessonMessages.SchedulePolicyMustBeSlotGrid);
        }

        return rental;
    }

    private async Task<(OccupancyKind Open, OccupancyKind Lesson)> LoadCanonicalKindsAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var kinds = await dbContext.OccupancyKinds
            .Where(k => k.TenantId == tenantId && k.IsActive && (k.Key == "open" || k.Key == "lesson"))
            .ToListAsync(cancellationToken);

        var open = kinds.SingleOrDefault(k => k.Key == "open")
            ?? throw new InvalidOperationException(TeacherLessonMessages.OpenKindMissing);
        var lesson = kinds.SingleOrDefault(k => k.Key == "lesson")
            ?? throw new InvalidOperationException(TeacherLessonMessages.LessonKindMissing);

        if (!open.IsBookableByCustomer || open.BlocksCapacity
            || lesson.IsBookableByCustomer || !lesson.BlocksCapacity)
        {
            throw new InvalidOperationException(TeacherLessonMessages.UnsafeKindFlags);
        }

        return (open, lesson);
    }

    private async Task EnsureStartNotInPastAsync(
        Guid tenantId,
        DateOnly date,
        TimeOnly startTime,
        CancellationToken cancellationToken)
    {
        var zone = await TenantBookingClock.ResolveForTenantAsync(
            dbContext, tenantId, cancellationToken);
        var start = TenantBookingClock.AtLocal(date, startTime, zone);
        ReservationBookingRules.EnsureStartNotBeforeNow(
            start,
            timeProvider,
            TeacherLessonMessages.FutureIntervalsOnly);
    }

    private static bool IsSlotStartUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "ix_slots_tenant_id_rental_asset_id_date_start_time",
        };

    private async Task EnsureActiveTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var active = await dbContext.Tenants
            .AsNoTracking()
            .AnyAsync(t => t.Id == tenantId && t.IsActive, cancellationToken);

        if (!active)
        {
            throw new InvalidOperationException(TeacherLessonMessages.TenantInactive);
        }
    }

    private Guid EnsureTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException(TeacherLessonMessages.TenantRequired);

    private void EnsurePilotScope(Guid tenantId, Guid rentalAssetId)
    {
        if (!teacherLessonScope.Allows(tenantId, rentalAssetId))
        {
            throw new KeyNotFoundException(TeacherLessonMessages.RentableNotFound);
        }
    }

    private static void ValidateTimeRange(TimeOnly start, TimeOnly end)
    {
        if (end <= start)
        {
            throw new ArgumentException(TeacherLessonMessages.EndAfterStart);
        }
    }

    private static string? TrimLabel(string? label) =>
        string.IsNullOrWhiteSpace(label) ? null : label.Trim();

    private static SlotResponseDto ToSlotDto(Slot slot) =>
        new(
            slot.Id,
            slot.RentalAssetId,
            slot.RentalAsset.Asset.Name,
            slot.Date,
            slot.StartTime,
            slot.EndTime,
            slot.OccupancyKindId,
            slot.OccupancyKind.Key,
            slot.OccupancyKind.Label,
            slot.OccupancyKind.ColorHex,
            slot.OccupancyKind.IsBookableByCustomer,
            slot.Label,
            slot.Status,
            slot.ReservationId,
            IsDerived: false,
            SlotOccurrenceSource.DailyOverride,
            slot.SourceTemplateId,
            slot.RentalAsset.SchedulePolicy,
            SupportsEntireRecurrence: slot.RentalAsset.SchedulePolicy == SchedulePolicy.SlotGrid);

    private static async Task CommitIfPresentAsync(
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private static async Task RollbackIfPresentAsync(
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (transaction is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
        }
    }
}
