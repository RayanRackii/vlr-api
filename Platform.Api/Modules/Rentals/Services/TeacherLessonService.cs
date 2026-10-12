using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Services.Trial;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Modules.Rentals.Services;

public sealed class TeacherLessonService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    ITrialGuard trialGuard,
    TimeProvider timeProvider,
    ITeacherLessonScope teacherLessonScope) : ITeacherLessonService
{
    private static readonly ReservationStatus[] BlockingStatuses =
    [
        ReservationStatus.PendingDeposit,
        ReservationStatus.Confirmed
    ];

    public async Task<SlotResponseDto> CreateAsync(
        CreateTeacherLessonRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        EnsurePilotScope(tenantId, request.RentalAssetId);
        await EnsureActiveTenantAsync(tenantId, cancellationToken);
        await trialGuard.EnsureWritableAsync(cancellationToken);
        ValidateTimeRange(request.StartTime, request.EndTime);
        EnsureFutureInterval(request.Date, request.StartTime);
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
                        "The interval overlaps an existing slot.");
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
                        "The interval overlaps a non-open or unverifiable slot.");
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
                throw new InvalidOperationException("The interval overlaps existing slots.");
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
            throw new InvalidOperationException("A slot already exists at this start time.", exception);
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
        EnsureFutureInterval(request.Date, request.StartTime);
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
                    "The interval overlaps an existing slot.");
            }

            var source = winning.Template;
            Slot? target = overlappingSlots.SingleOrDefault();
            if (target is null)
            {
                if (source.OccupancyKindId != lessonKind.Id
                    || !source.OccupancyKind.IsActive
                    || !string.Equals(source.OccupancyKind.Key, "lesson", StringComparison.Ordinal))
                {
                    throw new KeyNotFoundException("A weekly lesson was not found for this interval.");
                }
            }
            else if (target.OccupancyKindId == lessonKind.Id
                     && target.OccupancyKind.IsActive
                     && string.Equals(target.OccupancyKind.Key, "lesson", StringComparison.Ordinal)
                     && target.Status == SlotStatus.Available
                     && target.ReservationId is null
                     && target.SourceTemplateId is not null)
            {
                var sourceExists = await dbContext.ScheduleTemplates
                    .AsNoTracking()
                    .AnyAsync(t => t.Id == target.SourceTemplateId
                                   && t.TenantId == tenantId
                                   && t.RentalAssetId == request.RentalAssetId
                                   && t.DayOfWeek == request.Date.DayOfWeek
                                   && t.StartTime <= target.StartTime
                                   && t.EndTime >= target.EndTime,
                        cancellationToken);
                if (!sourceExists)
                {
                    throw new InvalidOperationException("The lesson source template is unverifiable.");
                }

                if (source.OccupancyKindId == lessonKind.Id
                    && target.SourceTemplateId != source.Id)
                {
                    throw new InvalidOperationException(
                        "A different weekly lesson now wins this interval.");
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
                throw new InvalidOperationException("Only an exact canonical lesson can be removed.");
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
            throw new InvalidOperationException("A slot already exists at this start time.", exception);
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
                "Requested interval is not an available open SlotGrid window.");
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
                "Requested interval does not match a unique SlotGrid window.");
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
                "The interval overlaps an active non-open template.");
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
        var start = BrazilTimeZone.AtLocal(date, startTime);
        var end = BrazilTimeZone.AtLocal(date, endTime);

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
                "The interval overlaps a blocking reservation.");
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
            ?? throw new KeyNotFoundException("Rentable was not found.");

        if (rental.SchedulePolicy != SchedulePolicy.SlotGrid)
        {
            throw new InvalidOperationException("Schedule policy must be SlotGrid.");
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
            ?? throw new InvalidOperationException("Canonical open occupancy kind is not available.");
        var lesson = kinds.SingleOrDefault(k => k.Key == "lesson")
            ?? throw new InvalidOperationException("Canonical lesson occupancy kind is not available.");

        if (!open.IsBookableByCustomer || open.BlocksCapacity
            || lesson.IsBookableByCustomer || !lesson.BlocksCapacity)
        {
            throw new InvalidOperationException("Canonical open and lesson occupancy kinds have unsafe flags.");
        }

        return (open, lesson);
    }

    private void EnsureFutureInterval(DateOnly date, TimeOnly startTime)
    {
        var now = timeProvider.GetUtcNow();
        var today = BrazilTimeZone.GetCivilDate(now);
        if (date < today)
        {
            throw new ArgumentException("Teacher lessons can only change future intervals.");
        }

        if (date == today)
        {
            var localNow = TimeZoneInfo.ConvertTime(now, BrazilTimeZone.Resolve());
            if (startTime <= TimeOnly.FromDateTime(localNow.DateTime))
            {
                throw new ArgumentException("Teacher lessons can only change future intervals.");
            }
        }
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
            throw new InvalidOperationException("Tenant is not active.");
        }
    }

    private Guid EnsureTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");

    private void EnsurePilotScope(Guid tenantId, Guid rentalAssetId)
    {
        if (!teacherLessonScope.Allows(tenantId, rentalAssetId))
        {
            throw new KeyNotFoundException("Rentable was not found.");
        }
    }

    private static void ValidateTimeRange(TimeOnly start, TimeOnly end)
    {
        if (end <= start)
        {
            throw new ArgumentException("End time must be after start time.");
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
