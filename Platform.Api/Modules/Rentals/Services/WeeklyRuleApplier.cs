using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;
using static Platform.Api.Modules.Rentals.Services.AvailabilityReadModel;
using static Platform.Api.Modules.Rentals.Services.ScheduleWriteGuards;
// Template grid key shared with the default seed (TemplateExactKey).
using static Platform.Api.Modules.Rentals.Services.ScheduleTemplateWriter;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>
/// Weekly rule: expands an open interval into slot-sized schedule templates for the
/// selected rentables and weekdays (exact matches are skipped).
/// </summary>
internal sealed class WeeklyRuleApplier(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IOccupancyKindService occupancyKindService)
{
    private readonly ScheduleWriteGuards guards = new(dbContext, tenantProvider);

    private readonly ScheduleTemplateWriter templates =
        new(dbContext, tenantProvider, occupancyKindService);

    public async Task<ApplyWeeklyRuleResponseDto> ApplyWeeklyRuleAsync(
        ApplyWeeklyRuleRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = guards.EnsureTenant();
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
        await guards.EnsureRentablesAsync(tenantId, rentableIds, cancellationToken);
        await guards.EnsureOccupancyKindAsync(tenantId, request.OccupancyKindId, cancellationToken);
        var label = TrimLabel(request.Label);

        await using var transaction = await guards.BeginAssetLockedTransactionAsync(
            tenantId, rentableIds, cancellationToken);
        try
        {
        var existing = await dbContext.ScheduleTemplates
            .Where(t => t.TenantId == tenantId
                        && rentableIds.Contains(t.RentalAssetId)
                        && request.DaysOfWeek.Contains(t.DayOfWeek))
            .ToListAsync(cancellationToken);

        var byExact = existing
            .GroupBy(t => new TemplateExactKey(
                t.RentalAssetId, t.DayOfWeek, t.StartTime, t.EndTime, t.OccupancyKindId))
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(t => t.Id).First());

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
                    if (end > request.CloseTime || end <= cursor)
                    {
                        break;
                    }

                    var key = new TemplateExactKey(
                        rentableId, dayOfWeek, cursor, end, request.OccupancyKindId);
                    if (byExact.ContainsKey(key))
                    {
                        skipped++;
                        cursor = end;
                        continue;
                    }

                    EnsureNoTemplateCollision(
                        existing,
                        rentableId,
                        dayOfWeek,
                        cursor,
                        end,
                        request.OccupancyKindId,
                        excludeId: null);

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
                    existing.Add(entity);
                    byExact[key] = entity;
                    created++;

                    cursor = end;
                }
            }
        }

        var policyChanged = await templates.EnsureSlotGridPolicyAsync(tenantId, rentableIds, cancellationToken);

        if (created > 0 || updated > 0 || policyChanged)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await CommitIfPresentAsync(transaction, cancellationToken);
        return new ApplyWeeklyRuleResponseDto(created, updated, skipped);
        }
        catch
        {
            await RollbackIfPresentAsync(transaction, cancellationToken);
            throw;
        }
    }
}
