using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Authorization;
using Platform.Api.Modules.ResourcePlanning.Dtos;
using Platform.Api.Services.Tenancy;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.ResourcePlanning.Services;

public interface IPlanningAgendaService
{
    Task<PlanningAgendaResponse> GetAgendaAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyCollection<Guid>? assetIds,
        Guid? unitId,
        CancellationToken cancellationToken);
}

/// <summary>
/// The planning agenda read. A fixed number of round trips whatever the number of assets or
/// allocations: modules, time zone, assets with their profile, their working hours, then the
/// allocations overlapping the range, filtered by the stored occupied window (nothing is padded
/// or joined per asset).
/// </summary>
public sealed class PlanningAgendaService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    ITenantModuleAccessor tenantModuleAccessor,
    TimeProvider timeProvider) : IPlanningAgendaService
{
    public const int MaxRangeDays = 42;
    public const int MaxAssetIds = 200;

    private const int MinYear = 2000;
    private const int MaxYear = 2999;

    public async Task<PlanningAgendaResponse> GetAgendaAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyCollection<Guid>? assetIds,
        Guid? unitId,
        CancellationToken cancellationToken)
    {
        ValidateRange(from, to);
        if (assetIds is { Count: > MaxAssetIds })
        {
            throw new PlanningException(
                StatusCodes.Status400BadRequest,
                PlanningException.InvalidRangeCode,
                $"At most {MaxAssetIds} assets can be requested at once.");
        }

        var tenantId = tenantProvider.TenantId is { } id && id != Guid.Empty
            ? id
            : throw new UnauthorizedAccessException("Tenant context is required.");
        var rangeStart = from.ToUniversalTime();
        var rangeEnd = to.ToUniversalTime();
        var now = timeProvider.GetUtcNow();

        var rentalsActive = (await tenantModuleAccessor.GetActiveModuleKeysAsync(cancellationToken))
            .Contains(PlatformModules.Rentals);
        var zone = await TenantTimeZoneProvider.ResolveForTenantAsync(dbContext, tenantId, cancellationToken);
        var zoneId = TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : zone.Id;

        var assets = dbContext.Assets
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.ScheduledDeletionAt == null);
        if (unitId is { } unit)
        {
            assets = assets.Where(a => a.UnitId == unit);
        }

        if (assetIds is { Count: > 0 })
        {
            var requested = assetIds.ToList();
            assets = assets.Where(a => requested.Contains(a.Id));
        }

        // Plannable assets, plus any asset that still holds a blocking allocation even if its
        // profile was set to not plannable afterwards: a started job must stay visible until it
        // is released. Profile joined in the one query (left join; a missing row reads as defaults).
        var rows = await (
                from a in assets
                join p0 in dbContext.AssetPlanningProfiles.AsNoTracking()
                    on new { a.TenantId, AssetId = a.Id } equals new { p0.TenantId, p0.AssetId } into profileJoin
                from p in profileJoin.DefaultIfEmpty()
                where (p != null && p.IsPlannable)
                    || dbContext.AssetAllocations.Any(x => x.TenantId == tenantId
                        && x.AssetId == a.Id
                        && (x.Status == AllocationStatus.Running
                            || x.Status == AllocationStatus.Cleaning
                            || (x.Status == AllocationStatus.Scheduled
                                && (x.Source != AssetAllocationSource.Manual || x.OccupiedEnd > now))))
                orderby a.Tag, a.Id
                select new
                {
                    a.Id,
                    a.Name,
                    a.Tag,
                    a.UnitId,
                    UnitName = a.Unit.Name,
                    Capacity = (int?)p.Capacity,
                    HoursPolicy = (AssetHoursPolicy?)p.HoursPolicy,
                    HasActiveRental = a.RentalConfiguration != null && a.RentalConfiguration.IsActive,
                })
            .ToListAsync(cancellationToken);

        var resourceIds = rows.Select(r => r.Id).ToList();

        var hours = resourceIds.Count == 0
            ? new List<AssetWorkingHours>()
            : await dbContext.AssetWorkingHours
                .AsNoTracking()
                .Where(h => h.TenantId == tenantId && resourceIds.Contains(h.AssetId))
                .ToListAsync(cancellationToken);
        var hoursByAsset = hours.ToLookup(h => h.AssetId);

        // Cancelled rows never show. A started allocation is held until released, so it is listed
        // whatever its stored end; everything else is listed when its stored window overlaps.
        var allocations = resourceIds.Count == 0
            ? new List<AssetAllocation>()
            : await dbContext.AssetAllocations
                .AsNoTracking()
                .Where(a => a.TenantId == tenantId
                    && resourceIds.Contains(a.AssetId)
                    && a.Status != AllocationStatus.Cancelled
                    && ((a.OccupiedStart < rangeEnd && a.OccupiedEnd > rangeStart)
                        || ((a.Status == AllocationStatus.Running || a.Status == AllocationStatus.Cleaning)
                            && (a.OccupiedStart < rangeEnd
                                || (a.ActualStartAt != null && a.ActualStartAt < rangeEnd)))))
                .OrderBy(a => a.OccupiedStart)
                .ThenBy(a => a.Id)
                .ToListAsync(cancellationToken);

        var names = rows.ToDictionary(r => r.Id, r => r.Name);
        var resources = rows
            .Select(r => new PlanningAgendaResourceDto(
                r.Id,
                r.Name,
                r.Tag,
                r.UnitId,
                r.UnitName,
                r.Capacity ?? 1,
                (r.HoursPolicy ?? AssetHoursPolicy.Anytime).ToString(),
                r.HasActiveRental && rentalsActive,
                zoneId,
                hoursByAsset[r.Id]
                    .OrderBy(h => h.DayOfWeek)
                    .ThenBy(h => h.OpenTime)
                    .Select(h => new AssetWorkingHoursWindowDto
                    {
                        DayOfWeek = h.DayOfWeek,
                        OpenTime = h.OpenTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                        CloseTime = h.CloseTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                    })
                    .ToList()))
            .ToList();

        var items = allocations
            .Select(a => AllocationMapper.ToResponse(a, names.GetValueOrDefault(a.AssetId, string.Empty), now))
            .ToList();

        // Customer reservations of rental-backed assets arrive with the rentals bridge.
        return new PlanningAgendaResponse(rangeStart, rangeEnd, resources, items, []);
    }

    private static void ValidateRange(DateTimeOffset from, DateTimeOffset to)
    {
        if (from.UtcDateTime.Year < MinYear || to.UtcDateTime.Year > MaxYear)
        {
            throw Invalid($"'from' and 'to' must be between years {MinYear} and {MaxYear}.");
        }

        if (from >= to)
        {
            throw Invalid("'from' must be strictly before 'to'.");
        }

        if (to - from > TimeSpan.FromDays(MaxRangeDays))
        {
            throw Invalid($"The requested range must not exceed {MaxRangeDays} days.");
        }

        static PlanningException Invalid(string message) =>
            new(StatusCodes.Status400BadRequest, PlanningException.InvalidRangeCode, message);
    }
}
