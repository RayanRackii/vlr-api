using Microsoft.EntityFrameworkCore;
using Platform.Api.Authorization;
using Platform.Api.Modules.Rentals.Contracts;
using Platform.Api.Modules.ResourcePlanning.Contracts;
using Platform.Api.Modules.ResourcePlanning.Dtos;
using Platform.Api.Services.Tenancy;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.ResourcePlanning.Services;

/// <summary>
/// The planning port. A planning-only asset is occupied by the <c>planning.asset_allocations</c>
/// table: overlap is computed over the stored occupied windows under the asset-row lock the caller
/// holds (<see cref="LockAssetsAsync"/>), so two writers on one asset are serialized and the second
/// sees the first one's committed row. A rental-backed asset (the rentals bridge) keeps the rental
/// schedule as the calendar authority: this class is then the only writer of both the allocation
/// and its mirror <c>InternalUse</c> reservation, through <see cref="IResourceOccupancyService"/>,
/// inside the caller's single transaction. Lock order: asset rows ascending, then rental-asset
/// rows ascending; no path in this class takes a rental-asset lock before an asset lock.
/// </summary>
public sealed class AssetOccupancyService(
    AppDbContext dbContext,
    ITenantModuleAccessor tenantModuleAccessor,
    IResourceOccupancyService rentalOccupancy,
    TimeProvider timeProvider) : IAssetOccupancyService
{
    private const int MinYear = 2000;
    private const int MaxYear = 2999;

    public async Task LockAssetsAsync(Guid tenantId, IEnumerable<Guid> assetIds, CancellationToken cancellationToken)
    {
        var ids = assetIds.Where(id => id != Guid.Empty).Distinct().ToList();

        // Asset rows first (spec 4.3), the rental-asset rows only after them.
        await AssetRowLock.LockAsync(dbContext, tenantId, ids, cancellationToken);
        if (ids.Count == 0 || !await IsRentalsActiveAsync(cancellationToken))
        {
            return;
        }

        var rentalAssetIds = await dbContext.RentalAssets
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && ids.Contains(r.AssetId))
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);
        await rentalOccupancy.LockResourcesAsync(dbContext, tenantId, rentalAssetIds, cancellationToken);
    }

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
                RentalAssetId = a.RentalConfiguration != null && a.RentalConfiguration.IsActive
                    ? (Guid?)a.RentalConfiguration.Id
                    : null,
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

        var zone = await TenantTimeZoneProvider.ResolveForTenantAsync(dbContext, tenantId, cancellationToken);
        var zoneId = TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : zone.Id;

        if (asset.RentalAssetId is { } rentalAssetId && await IsRentalsActiveAsync(cancellationToken))
        {
            // The rental schedule is the calendar authority: its hours and window rules apply (see
            // ValidateWindowAsync), the planning hours are not consulted, and a rental resource is
            // exclusive (capacity one).
            return new PlannableAsset(
                asset.Id,
                asset.UnitId,
                asset.Name,
                1,
                AssetHoursPolicy.Anytime,
                [],
                profile.DefaultPrepMinutes,
                profile.DefaultCleanupMinutes,
                zone,
                zoneId,
                rentalAssetId);
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

    public async Task<AllocationWindow> ValidateWindowAsync(
        Guid tenantId,
        PlannableAsset asset,
        AssetAllocationKind kind,
        DateTimeOffset workStart,
        DateTimeOffset workEnd,
        int? prepMinutes,
        int? cleanupMinutes,
        CancellationToken cancellationToken)
    {
        var window = ValidateWindow(asset, kind, workStart, workEnd, prepMinutes, cleanupMinutes);
        if (asset.RentalAssetId is not { } rentalAssetId)
        {
            return window;
        }

        // Rental-backed: the occupied window must be an offerable booking window within one day,
        // exactly as the catalog Phase 0 flow requires (Unavailable blocks included: they write the
        // same reservation). Single day also keeps it within 24 hours.
        var validation = await rentalOccupancy.ValidateWorkWindowAsync(
            dbContext, tenantId, rentalAssetId, window.OccupiedStart, window.OccupiedEnd, cancellationToken);
        return validation.Error is null ? window : window with { RentalWindowError = validation.Error };
    }

    public async Task<AvailabilityResult> CheckAvailabilityAsync(
        Guid tenantId,
        PlannableAsset asset,
        AssetAllocationKind kind,
        AllocationWindow window,
        Guid? excludeAllocationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(window);

        if (asset.RentalAssetId is { } rentalAssetId)
        {
            return await CheckBridgedAsync(
                tenantId, asset, rentalAssetId, window, excludeAllocationId, cancellationToken);
        }

        return await CheckCoreAsync(
            tenantId,
            asset.AssetId,
            asset.Capacity,
            kind,
            window.OccupiedStart,
            window.OccupiedEnd,
            excludeAllocationId,
            cancellationToken);
    }

    /// <summary>
    /// Rental-backed asset: the blockers are the blocking reservations of the rental asset, the
    /// customer bookings and the mirrors of other allocations alike (the same blocking rule rentals
    /// applies to a booking, so an allocation blocks a customer and the reverse), plus the
    /// allocations that predate the bridge and have no reservation. The allocation being edited and
    /// its own mirror are left out.
    /// </summary>
    private async Task<AvailabilityResult> CheckBridgedAsync(
        Guid tenantId,
        PlannableAsset asset,
        Guid rentalAssetId,
        AllocationWindow window,
        Guid? excludeAllocationId,
        CancellationToken cancellationToken)
    {
        Guid? ownItemId = null;
        if (excludeAllocationId is { } excluded)
        {
            ownItemId = await dbContext.AssetAllocations
                .AsNoTracking()
                .Where(a => a.TenantId == tenantId && a.Id == excluded)
                .Select(a => a.ReservationItemId)
                .FirstOrDefaultAsync(cancellationToken);
        }

        var windows = await rentalOccupancy.ListBlockingWindowsAsync(
            dbContext, tenantId, [rentalAssetId], window.OccupiedStart, window.OccupiedEnd, cancellationToken);
        var conflicts = await BuildReservationConflictsAsync(
            tenantId,
            windows.Where(w => ownItemId == null || w.ReservationItemId != ownItemId).ToList(),
            window.OccupiedEnd,
            cancellationToken);

        var legacy = await CheckCoreAsync(
            tenantId,
            asset.AssetId,
            1,
            AssetAllocationKind.Use,
            window.OccupiedStart,
            window.OccupiedEnd,
            excludeAllocationId,
            cancellationToken,
            withoutReservationOnly: true);

        return new AvailabilityResult(conflicts
            .Concat(legacy.Conflicts)
            .OrderBy(c => c.Start)
            .ThenBy(c => c.AllocationId)
            .ToList());
    }

    /// <summary>
    /// Names the blocking reservations: the mirror of an allocation is reported as that allocation
    /// (<c>allocation</c>, with its title); any other reservation (a customer booking, an order
    /// allocated through the catalog) is anonymous (<c>reservation</c>, no label).
    /// </summary>
    private async Task<List<AllocationConflictDto>> BuildReservationConflictsAsync(
        Guid tenantId,
        IReadOnlyList<ResourceOccupancyWindow> windows,
        DateTimeOffset requestedEnd,
        CancellationToken cancellationToken)
    {
        var conflicts = new List<AllocationConflictDto>();
        if (windows.Count == 0)
        {
            return conflicts;
        }

        var itemIds = windows.Select(w => (Guid?)w.ReservationItemId).ToList();
        var mirrors = await dbContext.AssetAllocations
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && itemIds.Contains(a.ReservationItemId))
            .Select(a => new { a.ReservationItemId, a.Id, a.Title, a.OccupiedStart, a.OccupiedEnd })
            .ToListAsync(cancellationToken);
        var mirrorByItem = mirrors
            .Where(m => m.ReservationItemId != null)
            .ToDictionary(m => m.ReservationItemId!.Value);

        foreach (var blocker in windows)
        {
            if (mirrorByItem.TryGetValue(blocker.ReservationItemId, out var mirror))
            {
                conflicts.Add(new AllocationConflictDto(
                    "allocation", mirror.Id, mirror.OccupiedStart, mirror.OccupiedEnd, mirror.Title));
            }
            else
            {
                // An overdue, unreleased internal-use reservation has no end: show the asked window's end.
                conflicts.Add(new AllocationConflictDto(
                    "reservation",
                    null,
                    blocker.Start,
                    blocker.End == DateTimeOffset.MaxValue ? requestedEnd : blocker.End,
                    null));
            }
        }

        return conflicts;
    }

    public async Task<AvailabilityResult> CheckStartAsync(
        Guid tenantId,
        AssetAllocation allocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(allocation);

        if (allocation.ReservationItemId is { } ownItemId)
        {
            // Bridged: the mirror reservation holds the occupied window and an early start does not
            // move it (as in the catalog Phase 0 flow), so the check covers that window, leaving the
            // allocation's own reservation out.
            var rentalAssetId = await dbContext.ReservationItems
                .AsNoTracking()
                .Where(i => i.TenantId == tenantId && i.Id == ownItemId)
                .Select(i => (Guid?)i.RentalAssetId)
                .FirstOrDefaultAsync(cancellationToken);
            if (rentalAssetId is not { } rentalId)
            {
                return new AvailabilityResult([]);
            }

            var windows = await rentalOccupancy.ListBlockingWindowsAsync(
                dbContext, tenantId, [rentalId], allocation.OccupiedStart, allocation.OccupiedEnd, cancellationToken);
            return new AvailabilityResult(await BuildReservationConflictsAsync(
                tenantId,
                windows.Where(w => w.ReservationItemId != ownItemId).ToList(),
                allocation.OccupiedEnd,
                cancellationToken));
        }

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
        CancellationToken cancellationToken,
        bool withoutReservationOnly = false)
    {
        // Candidates by the stored windows: a Scheduled one must end after the request starts; a
        // started one (Running, Cleaning) has no implicit end, so it only has to have begun.
        // On a rental-backed asset only the allocations without a reservation are read here; the
        // others are represented by their mirror reservation.
        var candidates = await dbContext.AssetAllocations
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId
                && a.AssetId == assetId
                && (excludeAllocationId == null || a.Id != excludeAllocationId)
                && (!withoutReservationOnly || a.ReservationItemId == null)
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

    public async Task<AssetAllocation> OccupyAsync(
        Guid tenantId,
        PlannableAsset asset,
        NewAllocation request,
        CancellationToken cancellationToken)
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

        if (asset.RentalAssetId is { } rentalAssetId)
        {
            // The mirror reservation, written in the same transaction as the allocation. The window
            // was checked under the lock; the rentals port revalidates it and a race lands as a
            // conflict.
            try
            {
                allocation.ReservationItemId = await rentalOccupancy.CreateInternalUseOccupancyAsync(
                    dbContext,
                    tenantId,
                    asset.UnitId,
                    rentalAssetId,
                    1,
                    request.Window.OccupiedStart,
                    request.Window.OccupiedEnd,
                    cancellationToken);
            }
            catch (ResourceUnavailableException)
            {
                throw ReservationConflict(request.Window);
            }
        }

        dbContext.AssetAllocations.Add(allocation);
        return allocation;
    }

    public async Task RescheduleAsync(
        Guid tenantId,
        AssetAllocation allocation,
        AllocationWindow window,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        ArgumentNullException.ThrowIfNull(window);

        if (allocation.ReservationItemId is not { } reservationItemId)
        {
            return;
        }

        try
        {
            await rentalOccupancy.RescheduleInternalUseOccupancyAsync(
                dbContext, tenantId, reservationItemId, window.OccupiedStart, window.OccupiedEnd, cancellationToken);
        }
        catch (ResourceUnavailableException)
        {
            throw ReservationConflict(window);
        }
    }

    public async Task ReleaseAsync(AssetAllocation allocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(allocation);

        if (allocation.ReservationItemId is not { } reservationItemId)
        {
            return;
        }

        // Never started: free the whole window. Started: close it at release plus cleanup. The same
        // two cases the catalog usage service maps for the Phase 0 InternalUse reservation.
        var kind = allocation.ActualStartAt is null
            ? InternalUseReleaseKind.CancelBeforeStart
            : InternalUseReleaseKind.ReleaseAfterStart;
        await rentalOccupancy.ReleaseInternalUseOccupancyAsync(
            dbContext,
            allocation.TenantId,
            reservationItemId,
            kind,
            allocation.PlannedWorkEnd,
            cancellationToken);
    }

    public async Task<IReadOnlyList<PlanningExternalBusyDto>> ListExternalBusyAsync(
        Guid tenantId,
        IReadOnlyDictionary<Guid, Guid> assetIdByRentalAssetId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assetIdByRentalAssetId);
        if (assetIdByRentalAssetId.Count == 0)
        {
            return [];
        }

        var windows = await rentalOccupancy.ListBlockingWindowsAsync(
            dbContext, tenantId, assetIdByRentalAssetId.Keys.ToList(), from, to, cancellationToken);
        if (windows.Count == 0)
        {
            return [];
        }

        // Reservations an allocation mirrors are already drawn as that allocation.
        var itemIds = windows.Select(w => (Guid?)w.ReservationItemId).ToList();
        var mirrorItemIds = await dbContext.AssetAllocations
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && itemIds.Contains(a.ReservationItemId))
            .Select(a => a.ReservationItemId)
            .ToListAsync(cancellationToken);
        var mirrored = mirrorItemIds
            .Where(id => id != null)
            .Select(id => id!.Value)
            .ToHashSet();

        return windows
            .Where(w => !mirrored.Contains(w.ReservationItemId))
            .Select(w => new PlanningExternalBusyDto(
                assetIdByRentalAssetId[w.RentalAssetId],
                w.Start < from ? from : w.Start,
                w.End > to ? to : w.End))
            .OrderBy(b => b.AssetId)
            .ThenBy(b => b.Start)
            .ToList();
    }

    private async Task<bool> IsRentalsActiveAsync(CancellationToken cancellationToken) =>
        (await tenantModuleAccessor.GetActiveModuleKeysAsync(cancellationToken)).Contains(PlatformModules.Rentals);

    private static PlanningException ReservationConflict(AllocationWindow window) =>
        PlanningException.Conflict(
            [new AllocationConflictDto("reservation", null, window.OccupiedStart, window.OccupiedEnd, null)]);

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
