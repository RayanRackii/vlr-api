using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using static Platform.Api.Modules.Rentals.Services.AvailabilityReadModel;
using static Platform.Api.Modules.Rentals.Services.ScheduleWriteGuards;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>
/// Weekly model writes: schedule template create/update/delete and the default grid seed.
/// Shared write guards live in <see cref="ScheduleWriteGuards"/>.
/// </summary>
internal sealed class ScheduleTemplateWriter(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IOccupancyKindService occupancyKindService)
{
    private readonly ScheduleWriteGuards guards = new(dbContext, tenantProvider);

    public async Task<ScheduleTemplateResponseDto> CreateTemplateAsync(
        UpsertScheduleTemplateRequestDto request,
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
            await guards.EnsureNoTemplateCollisionAsync(
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
        var tenantId = guards.EnsureTenant();
        ValidateTimeRange(request.StartTime, request.EndTime);

        var existing = await dbContext.ScheduleTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id && t.TenantId == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Schedule template was not found.");

        await guards.EnsureRentableAsync(tenantId, request.RentalAssetId, cancellationToken);
        await guards.EnsureOccupancyKindAsync(tenantId, request.OccupancyKindId, cancellationToken);

        var lockedAssetIds = new[] { existing.RentalAssetId, request.RentalAssetId };
        await using var transaction = await guards.BeginAssetLockedTransactionAsync(
            tenantId, lockedAssetIds, cancellationToken);
        try
        {
            var entity = await dbContext.ScheduleTemplates
                .FirstOrDefaultAsync(t => t.Id == id && t.TenantId == tenantId, cancellationToken)
                ?? throw new KeyNotFoundException("Schedule template was not found.");
            EnsureCourtStillLocked(entity.RentalAssetId, lockedAssetIds);

            await guards.EnsureNoTemplateCollisionAsync(
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
        var tenantId = guards.EnsureTenant();
        var existing = await dbContext.ScheduleTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id && t.TenantId == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Schedule template was not found.");

        var lockedAssetIds = new[] { existing.RentalAssetId };
        await using var transaction = await guards.BeginAssetLockedTransactionAsync(
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
        var tenantId = guards.EnsureTenant();
        await occupancyKindService.EnsureDefaultsAsync(cancellationToken);

        var rentableIds = ResolveRentableIds(request.RentalAssetId, request.RentalAssetIds);
        await guards.EnsureRentablesAsync(tenantId, rentableIds, cancellationToken);

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

        await using var transaction = await guards.BeginAssetLockedTransactionAsync(
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
