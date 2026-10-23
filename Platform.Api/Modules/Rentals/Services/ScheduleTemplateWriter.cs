using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using static Platform.Api.Modules.Rentals.Services.AvailabilityReadModel;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>
/// Weekly model writes: schedule template create/update/delete and the default grid seed.
/// Also hosts the schedule write guards (asset lock transaction, rentable/kind checks,
/// template collision) that <see cref="ScheduleService"/> still shares.
/// </summary>
internal sealed class ScheduleTemplateWriter(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IOccupancyKindService occupancyKindService)
{
    public async Task<ScheduleTemplateResponseDto> CreateTemplateAsync(
        UpsertScheduleTemplateRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        ValidateTimeRange(request.StartTime, request.EndTime);
        await EnsureRentableAsync(tenantId, request.RentalAssetId, cancellationToken);
        await EnsureOccupancyKindAsync(tenantId, request.OccupancyKindId, cancellationToken);

        await using var transaction = await BeginAssetLockedTransactionAsync(
            tenantId, [request.RentalAssetId], cancellationToken);
        try
        {
            await EnsureNoTemplateCollisionAsync(
                tenantId,
                request.RentalAssetId,
                request.DayOfWeek,
                request.StartTime,
                request.EndTime,
                request.OccupancyKindId,
                excludeId: null,
                cancellationToken);

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
            await CommitIfPresentAsync(transaction, cancellationToken);

            return await GetTemplateDtoAsync(entity.Id, cancellationToken);
        }
        catch
        {
            await RollbackIfPresentAsync(transaction, cancellationToken);
            throw;
        }
    }

    public async Task<ScheduleTemplateResponseDto> UpdateTemplateAsync(
        Guid id,
        UpsertScheduleTemplateRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        ValidateTimeRange(request.StartTime, request.EndTime);

        var existing = await dbContext.ScheduleTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id && t.TenantId == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Schedule template was not found.");

        await EnsureRentableAsync(tenantId, request.RentalAssetId, cancellationToken);
        await EnsureOccupancyKindAsync(tenantId, request.OccupancyKindId, cancellationToken);

        var lockedAssetIds = new[] { existing.RentalAssetId, request.RentalAssetId };
        await using var transaction = await BeginAssetLockedTransactionAsync(
            tenantId, lockedAssetIds, cancellationToken);
        try
        {
            var entity = await dbContext.ScheduleTemplates
                .FirstOrDefaultAsync(t => t.Id == id && t.TenantId == tenantId, cancellationToken)
                ?? throw new KeyNotFoundException("Schedule template was not found.");
            EnsureCourtStillLocked(entity.RentalAssetId, lockedAssetIds);

            await EnsureNoTemplateCollisionAsync(
                tenantId,
                request.RentalAssetId,
                request.DayOfWeek,
                request.StartTime,
                request.EndTime,
                request.OccupancyKindId,
                excludeId: id,
                cancellationToken);

            entity.RentalAssetId = request.RentalAssetId;
            entity.DayOfWeek = request.DayOfWeek;
            entity.StartTime = request.StartTime;
            entity.EndTime = request.EndTime;
            entity.OccupancyKindId = request.OccupancyKindId;
            entity.Label = TrimLabel(request.Label);
            entity.IsActive = request.IsActive;
            entity.Touch();

            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitIfPresentAsync(transaction, cancellationToken);
            return await GetTemplateDtoAsync(entity.Id, cancellationToken);
        }
        catch
        {
            await RollbackIfPresentAsync(transaction, cancellationToken);
            throw;
        }
    }

    public async Task DeleteTemplateAsync(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        var existing = await dbContext.ScheduleTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id && t.TenantId == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Schedule template was not found.");

        var lockedAssetIds = new[] { existing.RentalAssetId };
        await using var transaction = await BeginAssetLockedTransactionAsync(
            tenantId, lockedAssetIds, cancellationToken);
        try
        {
            var entity = await dbContext.ScheduleTemplates
                .FirstOrDefaultAsync(t => t.Id == id && t.TenantId == tenantId, cancellationToken)
                ?? throw new KeyNotFoundException("Schedule template was not found.");
            EnsureCourtStillLocked(entity.RentalAssetId, lockedAssetIds);

            dbContext.ScheduleTemplates.Remove(entity);
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitIfPresentAsync(transaction, cancellationToken);
        }
        catch
        {
            await RollbackIfPresentAsync(transaction, cancellationToken);
            throw;
        }
    }

    public async Task<SeedDefaultTemplatesResponseDto> SeedDefaultTemplatesAsync(
        SeedDefaultTemplatesRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        await occupancyKindService.EnsureDefaultsAsync(cancellationToken);

        var rentableIds = ResolveRentableIds(request.RentalAssetId, request.RentalAssetIds);
        await EnsureRentablesAsync(tenantId, rentableIds, cancellationToken);

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

        await using var transaction = await BeginAssetLockedTransactionAsync(
            tenantId, rentableIds, cancellationToken);
        try
        {
        OccupancyKind openKind;
        if (request.OccupancyKindId is { } kindId)
        {
            openKind = await dbContext.OccupancyKinds
                .FirstOrDefaultAsync(
                    k => k.Id == kindId && k.TenantId == tenantId && k.IsActive,
                    cancellationToken)
                ?? throw new KeyNotFoundException("Occupancy kind was not found.");
        }
        else
        {
            openKind = await dbContext.OccupancyKinds
                .FirstOrDefaultAsync(
                    k => k.TenantId == tenantId
                         && k.Key == "open"
                         && k.IsActive
                         && k.IsBookableByCustomer,
                    cancellationToken)
                ?? await dbContext.OccupancyKinds
                    .FirstOrDefaultAsync(
                        k => k.TenantId == tenantId && k.IsActive && k.IsBookableByCustomer,
                        cancellationToken)
                ?? throw new InvalidOperationException(
                    "No bookable occupancy kind is available for the default grid.");
        }

        var existingTemplates = await dbContext.ScheduleTemplates
            .Where(t => t.TenantId == tenantId && rentableIds.Contains(t.RentalAssetId))
            .ToListAsync(cancellationToken);

        var existingKeys = existingTemplates
            .Select(row => new TemplateExactKey(
                row.RentalAssetId, row.DayOfWeek, row.StartTime, row.EndTime, row.OccupancyKindId))
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
                    if (end > close || end <= cursor)
                    {
                        break;
                    }

                    var key = new TemplateExactKey(
                        rentableId, dayOfWeek, cursor, end, openKind.Id);
                    if (existingKeys.Contains(key))
                    {
                        skipped++;
                        cursor = end;
                        continue;
                    }

                    EnsureNoTemplateCollision(
                        existingTemplates,
                        rentableId,
                        dayOfWeek,
                        cursor,
                        end,
                        openKind.Id,
                        excludeId: null);

                    var seeded = new ScheduleTemplate
                    {
                        TenantId = tenantId,
                        RentalAssetId = rentableId,
                        DayOfWeek = dayOfWeek,
                        StartTime = cursor,
                        EndTime = end,
                        OccupancyKindId = openKind.Id,
                        Label = null,
                        IsActive = true,
                    };
                    dbContext.ScheduleTemplates.Add(seeded);
                    existingTemplates.Add(seeded);
                    existingKeys.Add(key);
                    created++;
                    cursor = end;
                }
            }
        }

        var policyChanged = await EnsureSlotGridPolicyAsync(tenantId, rentableIds, cancellationToken);

        if (created > 0 || policyChanged)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await CommitIfPresentAsync(transaction, cancellationToken);
        return new SeedDefaultTemplatesResponseDto(created, skipped);
        }
        catch
        {
            await RollbackIfPresentAsync(transaction, cancellationToken);
            throw;
        }
    }

    internal async Task<bool> EnsureSlotGridPolicyAsync(
        Guid tenantId,
        IReadOnlyList<Guid> rentableIds,
        CancellationToken cancellationToken)
    {
        var rentables = await dbContext.RentalAssets
            .Where(r => r.TenantId == tenantId && rentableIds.Contains(r.Id))
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

    internal async Task EnsureRentableAsync(
        Guid tenantId,
        Guid rentalAssetId,
        CancellationToken cancellationToken)
    {
        var exists = await dbContext.RentalAssets
            .AnyAsync(
                r => r.Id == rentalAssetId && r.TenantId == tenantId && r.IsActive,
                cancellationToken);
        if (!exists)
        {
            throw new KeyNotFoundException("Rentable was not found.");
        }
    }

    internal async Task EnsureRentablesAsync(
        Guid tenantId,
        IReadOnlyList<Guid> rentalAssetIds,
        CancellationToken cancellationToken)
    {
        if (rentalAssetIds.Count == 0)
        {
            throw new ArgumentException("At least one rentable is required.");
        }

        var found = await dbContext.RentalAssets
            .Where(r => rentalAssetIds.Contains(r.Id) && r.TenantId == tenantId && r.IsActive)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

        if (found.Count != rentalAssetIds.Count)
        {
            throw new KeyNotFoundException("One or more rentables were not found.");
        }
    }

    internal async Task EnsureOccupancyKindAsync(
        Guid tenantId,
        Guid occupancyKindId,
        CancellationToken cancellationToken)
    {
        var exists = await dbContext.OccupancyKinds
            .AnyAsync(
                k => k.Id == occupancyKindId && k.TenantId == tenantId && k.IsActive,
                cancellationToken);
        if (!exists)
        {
            throw new KeyNotFoundException("Occupancy kind was not found.");
        }
    }

    internal async Task EnsureNoTemplateCollisionAsync(
        Guid tenantId,
        Guid rentalAssetId,
        DayOfWeek dayOfWeek,
        TimeOnly start,
        TimeOnly end,
        Guid occupancyKindId,
        Guid? excludeId,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.ScheduleTemplates
            .Where(t => t.TenantId == tenantId
                        && t.RentalAssetId == rentalAssetId
                        && t.DayOfWeek == dayOfWeek)
            .ToListAsync(cancellationToken);

        EnsureNoTemplateCollision(
            existing, rentalAssetId, dayOfWeek, start, end, occupancyKindId, excludeId);
    }

    internal static void EnsureNoTemplateCollision(
        IEnumerable<ScheduleTemplate> existing,
        Guid rentalAssetId,
        DayOfWeek dayOfWeek,
        TimeOnly start,
        TimeOnly end,
        Guid occupancyKindId,
        Guid? excludeId)
    {
        foreach (var other in existing)
        {
            if (other.RentalAssetId != rentalAssetId || other.DayOfWeek != dayOfWeek)
            {
                continue;
            }

            if (excludeId is { } id && other.Id == id)
            {
                continue;
            }

            if (other.StartTime == start
                && other.EndTime == end
                && other.OccupancyKindId == occupancyKindId)
            {
                throw new ArgumentException(
                    "A schedule template with the same interval and occupancy kind already exists.");
            }

            if (other.OccupancyKindId == occupancyKindId
                && OccupancyPrecedence.IntervalsOverlap(start, end, other.StartTime, other.EndTime))
            {
                throw new ArgumentException(
                    "This occupancy kind already overlaps that interval on this weekday.");
            }
        }
    }

    internal readonly record struct TemplateExactKey(
        Guid RentalAssetId,
        DayOfWeek DayOfWeek,
        TimeOnly StartTime,
        TimeOnly EndTime,
        Guid OccupancyKindId);

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

    internal async Task<IDbContextTransaction?> BeginAssetLockedTransactionAsync(
        Guid tenantId,
        IEnumerable<Guid> rentalAssetIds,
        CancellationToken cancellationToken)
    {
        var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            await RentalAssetLocks.LockByRentalAssetIdsAsync(
                dbContext,
                tenantId,
                rentalAssetIds,
                cancellationToken);
            return transaction;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
    }

    internal static async Task CommitIfPresentAsync(
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }

    internal static async Task RollbackIfPresentAsync(
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (transaction is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
        }
    }

    private Guid EnsureTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");

    internal static void ValidateTimeRange(TimeOnly start, TimeOnly end)
    {
        if (end <= start)
        {
            throw new ArgumentException("End time must be after start time.");
        }
    }

    private static void EnsureCourtStillLocked(
        Guid currentRentalAssetId,
        IReadOnlyCollection<Guid> lockedAssetIds)
    {
        if (!lockedAssetIds.Contains(currentRentalAssetId))
        {
            throw new InvalidOperationException(
                "The template court changed before its row was locked.");
        }
    }

    internal static ScheduleTemplateResponseDto ToTemplateDto(ScheduleTemplate t) =>
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
}
