using Microsoft.EntityFrameworkCore;
using Platform.Api.Authorization;
using Platform.Api.Modules.ResourcePlanning.Contracts;
using Platform.Api.Modules.ResourcePlanning.Dtos;
using Platform.Api.Services.Tenancy;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.ResourcePlanning.Services;

/// <summary>
/// Planning-only occupancy backend: the <c>planning.asset_allocations</c> table is the calendar of
/// the asset. Overlap is computed over the stored occupied windows under the asset-row lock the
/// caller holds (<see cref="LockAssetsAsync"/>), so two writers on one asset are serialized and the
/// second sees the first one's committed row.
/// </summary>
public sealed class AssetOccupancyService(
    AppDbContext dbContext,
    ITenantModuleAccessor tenantModuleAccessor,
    TimeProvider timeProvider) : IAssetOccupancyService
{
    private const int MinYear = 2000;
    private const int MaxYear = 2999;

    public Task LockAssetsAsync(Guid tenantId, IEnumerable<Guid> assetIds, CancellationToken cancellationToken) =>
        AssetRowLock.LockAsync(dbContext, tenantId, assetIds, cancellationToken);

    public async Task<PlannableAsset> ResolvePlannableAssetAsync(
        Guid tenantId,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("Tenant context is required.");
        }

        var asset = await dbContext.Assets
            .AsNoTracking()
            .Where(a => a.Id == assetId && a.TenantId == tenantId && a.ScheduledDeletionAt == null)
            .Select(a => new
            {
                a.Id,
                a.UnitId,
                a.Name,
                a.Status,
                HasActiveRental = a.RentalConfiguration != null && a.RentalConfiguration.IsActive,
            })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw PlanningException.AssetNotFound(assetId);

        var profile = await dbContext.AssetPlanningProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.AssetId == assetId, cancellationToken);

        if (asset.Status != AssetStatus.Active)
        {
            throw PlanningException.NotPlannable("The asset is not active, so it cannot be planned.");
        }

        if (profile is not { IsPlannable: true })
        {
            throw PlanningException.NotPlannable("The asset is not marked as plannable.");
        }

        if (asset.HasActiveRental
            && (await tenantModuleAccessor.GetActiveModuleKeysAsync(cancellationToken)).Contains(PlatformModules.Rentals))
        {
            throw PlanningException.NotPlannable(
                "The asset is rental-backed: its rental schedule is the calendar authority, so it cannot be planned here yet.");
        }

        var hours = await dbContext.AssetWorkingHours
            .AsNoTracking()
            .Where(h => h.TenantId == tenantId && h.AssetId == assetId)
            .ToListAsync(cancellationToken);

        if (profile.HoursPolicy != AssetHoursPolicy.Anytime && hours.Count == 0)
        {
            throw PlanningException.NotPlannable(
                "The asset has no working hours and its policy needs them, so it is closed all week.");
        }

        var zone = await TenantTimeZoneProvider.ResolveForTenantAsync(dbContext, tenantId, cancellationToken);
        var zoneId = TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : zone.Id;

        return new PlannableAsset(
            asset.Id,
            asset.UnitId,
            asset.Name,
            profile.Capacity,
            profile.HoursPolicy,
            hours.Select(h => new WorkingHoursWindow((DayOfWeek)h.DayOfWeek, h.OpenTime, h.CloseTime)).ToList(),
            profile.DefaultPrepMinutes,
            profile.DefaultCleanupMinutes,
            zone,
            zoneId);
    }

    public AllocationWindow ValidateWindow(
        PlannableAsset asset,
        AssetAllocationKind kind,
        DateTimeOffset workStart,
        DateTimeOffset workEnd,
        int? prepMinutes,
        int? cleanupMinutes)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var start = workStart.ToUniversalTime();
        var end = workEnd.ToUniversalTime();
        if (start.Year < MinYear || end.Year > MaxYear)
        {
            throw PlanningException.InvalidAllocation($"Dates must be between years {MinYear} and {MaxYear}.");
        }

        if (end <= start)
        {
            throw PlanningException.InvalidAllocation("The work window must end after it starts.");
        }

        if (end - start > TimeSpan.FromMinutes(AssetAllocationRules.MaxDurationMinutes))
        {
            throw PlanningException.InvalidAllocation(
                $"The work window must not exceed {AssetAllocationRules.MaxDurationMinutes / 1440} days.");
        }

        var prep = prepMinutes ?? asset.DefaultPrepMinutes;
        var cleanup = cleanupMinutes ?? asset.DefaultCleanupMinutes;
        ValidateBuffer(prep, "Prep minutes");
        ValidateBuffer(cleanup, "Cleanup minutes");

        var violation = kind == AssetAllocationKind.Unavailable
            ? null
            : AssetWorkingHoursRules.Validate(asset.HoursPolicy, asset.WorkingHours, asset.Zone, start, end);

        return new AllocationWindow(
            start,
            end,
            start.AddMinutes(-prep),
            end.AddMinutes(cleanup),
            violation);
    }

    public Task<AvailabilityResult> CheckAvailabilityAsync(
        Guid tenantId,
        PlannableAsset asset,
        AssetAllocationKind kind,
        AllocationWindow window,
        Guid? excludeAllocationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(window);

        return CheckCoreAsync(
            tenantId,
            asset.AssetId,
            asset.Capacity,
            kind,
            window.OccupiedStart,
            window.OccupiedEnd,
            excludeAllocationId,
            cancellationToken);
    }

    public async Task<AvailabilityResult> CheckStartAsync(
        Guid tenantId,
        AssetAllocation allocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(allocation);

        var capacity = await dbContext.AssetPlanningProfiles
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.AssetId == allocation.AssetId)
            .Select(p => (int?)p.Capacity)
            .FirstOrDefaultAsync(cancellationToken) ?? 1;

        var now = timeProvider.GetUtcNow();
        var start = allocation.OccupiedStart < now ? allocation.OccupiedStart : now;
        var end = allocation.OccupiedEnd > now ? allocation.OccupiedEnd : now.AddTicks(1);
        return await CheckCoreAsync(
            tenantId,
            allocation.AssetId,
            capacity,
            allocation.Kind,
            start,
            end,
            allocation.Id,
            cancellationToken);
    }

    private async Task<AvailabilityResult> CheckCoreAsync(
        Guid tenantId,
        Guid assetId,
        int capacity,
        AssetAllocationKind kind,
        DateTimeOffset start,
        DateTimeOffset end,
        Guid? excludeAllocationId,
        CancellationToken cancellationToken)
    {

        // Candidates by the stored windows: a Scheduled one must end after the request starts; a
        // started one (Running, Cleaning) has no implicit end, so it only has to have begun.
        var candidates = await dbContext.AssetAllocations
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId
                && a.AssetId == assetId
                && (excludeAllocationId == null || a.Id != excludeAllocationId)
                && ((a.Status == AllocationStatus.Scheduled && a.OccupiedStart < end && a.OccupiedEnd > start)
                    || ((a.Status == AllocationStatus.Running || a.Status == AllocationStatus.Cleaning)
                        && (a.OccupiedStart < end || (a.ActualStartAt != null && a.ActualStartAt < end)))))
            .ToListAsync(cancellationToken);

        var now = timeProvider.GetUtcNow();
        var blockers = candidates
            .Where(a => AssetAllocationRules.IsBlocking(a, now))
            .Select(a => new Blocker(
                a,
                a.Kind == AssetAllocationKind.Unavailable ? capacity : a.Quantity,
                AssetAllocationRules.BlockingStart(a) < start ? start : AssetAllocationRules.BlockingStart(a),
                AssetAllocationRules.BlockingEnd(a) > end ? end : AssetAllocationRules.BlockingEnd(a)))
            .Where(b => b.Start < b.End)
            .ToList();

        var requested = kind == AssetAllocationKind.Unavailable ? capacity : 1;
        var conflicting = FindConflicts(blockers, requested, capacity);

        return new AvailabilityResult(conflicting
            .OrderBy(a => a.OccupiedStart)
            .ThenBy(a => a.Id)
            .Select(a => new AllocationConflictDto(
                "allocation",
                a.Id,
                a.OccupiedStart,
                a.OccupiedEnd,
                a.Title))
            .ToList());
    }

    public AssetAllocation Occupy(Guid tenantId, PlannableAsset asset, NewAllocation request)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(request);

        var allocation = new AssetAllocation
        {
            TenantId = tenantId,
            AssetId = asset.AssetId,
            UnitId = asset.UnitId,
            Kind = request.Kind,
            Title = request.Title,
            Notes = request.Notes,
            Quantity = 1,
            PlannedWorkStart = request.Window.WorkStart,
            PlannedWorkEnd = request.Window.WorkEnd,
            OccupiedStart = request.Window.OccupiedStart,
            OccupiedEnd = request.Window.OccupiedEnd,
            TimeZoneId = asset.TimeZoneId,
            Status = AllocationStatus.Scheduled,
            Source = AssetAllocationSource.Manual,
            CreatedByUserId = request.CreatedByUserId,
            ActorId = request.CreatedByUserId,
        };
        dbContext.AssetAllocations.Add(allocation);
        return allocation;
    }

    public Task ReleaseAsync(AssetAllocation allocation, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// Sweep over the blockers clipped to the request window: wherever the quantity held plus the
    /// requested quantity exceeds the capacity, every blocker active at that point is a conflict.
    /// Ends sort before starts at the same instant, so windows are half-open.
    /// </summary>
    private static List<AssetAllocation> FindConflicts(List<Blocker> blockers, int requested, int capacity)
    {
        var points = new List<(DateTimeOffset At, bool IsStart, Blocker Blocker)>(blockers.Count * 2);
        foreach (var blocker in blockers)
        {
            points.Add((blocker.Start, true, blocker));
            points.Add((blocker.End, false, blocker));
        }

        points.Sort((a, b) =>
        {
            var byTime = a.At.CompareTo(b.At);
            return byTime != 0 ? byTime : a.IsStart.CompareTo(b.IsStart);
        });

        var active = new List<Blocker>();
        var conflicts = new Dictionary<Guid, AssetAllocation>();
        foreach (var (_, isStart, blocker) in points)
        {
            if (!isStart)
            {
                active.Remove(blocker);
                continue;
            }

            active.Add(blocker);
            if (active.Sum(b => b.Quantity) + requested > capacity)
            {
                foreach (var held in active)
                {
                    conflicts[held.Allocation.Id] = held.Allocation;
                }
            }
        }

        return [.. conflicts.Values];
    }

    private static void ValidateBuffer(int minutes, string label)
    {
        if (minutes < 0 || minutes > PlanningProfileService.MaxPrepOrCleanupMinutes)
        {
            throw PlanningException.InvalidAllocation(
                $"{label} must be between 0 and {PlanningProfileService.MaxPrepOrCleanupMinutes}.");
        }
    }

    private sealed record Blocker(AssetAllocation Allocation, int Quantity, DateTimeOffset Start, DateTimeOffset End);
}
