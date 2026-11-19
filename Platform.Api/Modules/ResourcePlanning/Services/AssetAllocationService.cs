using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.ResourcePlanning.Contracts;
using Platform.Api.Modules.ResourcePlanning.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.ResourcePlanning.Services;

public interface IAssetAllocationService
{
    Task<AllocationPreviewResponse> PreviewAsync(
        CreateAssetAllocationRequest request,
        CancellationToken cancellationToken);

    Task<AssetAllocationResponse> CreateAsync(
        CreateAssetAllocationRequest request,
        CancellationToken cancellationToken);

    Task<AssetAllocationResponse> GetAsync(Guid allocationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AssetAllocationResponse>> ListAsync(
        Guid? assetId,
        string? status,
        CancellationToken cancellationToken);

    Task<AssetAllocationResponse> UpdateAsync(
        Guid allocationId,
        UpdateAssetAllocationRequest request,
        CancellationToken cancellationToken);

    Task<AssetAllocationResponse> ApplyUsageAsync(
        Guid allocationId,
        AssetAllocationUsageRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Manual allocations on plannable assets: create, edit, read and the usage transitions. Every
/// write runs in one transaction that first takes the asset-row lock (the one serialization point,
/// shared with the planning profile), then re-reads what it validates. Every query carries the
/// tenant id on top of the global query filter.
/// </summary>
public sealed class AssetAllocationService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IAssetOccupancyService occupancy,
    IPlanningCallerAccessor callerAccessor,
    TimeProvider timeProvider) : IAssetAllocationService
{
    public const int MaxTitleLength = 200;
    public const int MaxNotesLength = 1000;
    public const int MaxReasonLength = 1000;
    public const int MaxListResults = 500;

    public async Task<AllocationPreviewResponse> PreviewAsync(
        CreateAssetAllocationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = EnsureTenantContext();
        var kind = ParseKind(request.Kind);

        var asset = await occupancy.ResolvePlannableAssetAsync(tenantId, request.AssetId, cancellationToken);
        var window = occupancy.ValidateWindow(
            asset, kind, request.WorkStart, request.WorkEnd, request.PrepMinutes, request.CleanupMinutes);
        var availability = await occupancy.CheckAvailabilityAsync(
            tenantId, asset, kind, window, excludeAllocationId: null, cancellationToken);

        return new AllocationPreviewResponse(
            window.HoursViolation is null && availability.IsFree,
            window.OccupiedStart,
            window.OccupiedEnd,
            availability.Conflicts,
            window.HoursViolation);
    }

    public async Task<AssetAllocationResponse> CreateAsync(
        CreateAssetAllocationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = EnsureTenantContext();
        var kind = ParseKind(request.Kind);
        var title = NormalizeTitle(request.Title);
        var notes = NormalizeNotes(request.Notes);
        var caller = await callerAccessor.GetAsync(cancellationToken);
        EnsureMayClose(caller, kind);

        await using var transaction = await BeginAsync(cancellationToken);
        // Lock order (spec 4.3): the tenant-modules lock first, then the asset row.
        await PlanningModuleGuard.LockAndRequireActiveAsync(dbContext, tenantId, cancellationToken);
        await occupancy.LockAssetsAsync(tenantId, [request.AssetId], cancellationToken);

        // Everything below is read after the lock: a concurrent create on this asset has either
        // committed (and is visible to the overlap query) or is waiting behind us.
        var asset = await occupancy.ResolvePlannableAssetAsync(tenantId, request.AssetId, cancellationToken);
        var window = occupancy.ValidateWindow(
            asset, kind, request.WorkStart, request.WorkEnd, request.PrepMinutes, request.CleanupMinutes);
        if (window.HoursViolation is { } violation)
        {
            throw PlanningException.OutsideWorkingHours(violation);
        }

        var availability = await occupancy.CheckAvailabilityAsync(
            tenantId, asset, kind, window, excludeAllocationId: null, cancellationToken);
        if (!availability.IsFree)
        {
            throw PlanningException.Conflict(availability.Conflicts);
        }

        var allocation = occupancy.Occupy(
            tenantId, asset, new NewAllocation(kind, title, notes, window, caller.UserId));

        await SaveAsync(request.AssetId, cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return AllocationMapper.ToResponse(allocation, asset.Name, timeProvider.GetUtcNow());
    }

    public async Task<AssetAllocationResponse> GetAsync(Guid allocationId, CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();
        var allocation = await dbContext.AssetAllocations
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == allocationId && a.TenantId == tenantId, cancellationToken)
            ?? throw PlanningException.AllocationNotFound(allocationId);

        var assetName = await AssetNameAsync(tenantId, allocation.AssetId, cancellationToken);
        return AllocationMapper.ToResponse(allocation, assetName, timeProvider.GetUtcNow());
    }

    public async Task<IReadOnlyList<AssetAllocationResponse>> ListAsync(
        Guid? assetId,
        string? status,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();
        var query = dbContext.AssetAllocations
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId);

        if (assetId is { } asset)
        {
            query = query.Where(a => a.AssetId == asset);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var parsed = ParseStatus(status);
            query = query.Where(a => a.Status == parsed);
        }

        var allocations = await query
            .OrderByDescending(a => a.OccupiedStart)
            .ThenBy(a => a.Id)
            .Take(MaxListResults)
            .ToListAsync(cancellationToken);

        var assetIds = allocations.Select(a => a.AssetId).Distinct().ToList();
        var names = await dbContext.Assets
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && assetIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Name, cancellationToken);

        var now = timeProvider.GetUtcNow();
        return allocations
            .Select(a => AllocationMapper.ToResponse(a, names.GetValueOrDefault(a.AssetId, string.Empty), now))
            .ToList();
    }

    public async Task<AssetAllocationResponse> UpdateAsync(
        Guid allocationId,
        UpdateAssetAllocationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = EnsureTenantContext();
        var expectedVersion = RequireVersion(request.ExpectedVersion);
        var title = request.Title is null ? null : NormalizeTitle(request.Title);
        // An empty string clears the notes (NormalizeNotes returns null for it); null leaves them.
        var notesChanged = request.Notes is not null;
        var notes = notesChanged ? NormalizeNotes(request.Notes) : null;

        var caller = await callerAccessor.GetAsync(cancellationToken);
        var assetId = await FindAssetIdAsync(tenantId, allocationId, cancellationToken);

        await using var transaction = await BeginAsync(cancellationToken);
        await PlanningModuleGuard.LockAndRequireActiveAsync(dbContext, tenantId, cancellationToken);
        await occupancy.LockAssetsAsync(tenantId, [assetId], cancellationToken);

        var allocation = await LoadFreshAsync(tenantId, allocationId, cancellationToken);
        EnsureMayClose(caller, allocation.Kind);
        EnsureManual(allocation);
        EnsureVersion(allocation, expectedVersion);
        if (allocation.Status != AllocationStatus.Scheduled)
        {
            throw PlanningException.InvalidTransition(
                $"Only a Scheduled allocation can be edited; this one is {allocation.Status}.");
        }

        var windowChanged = request.WorkStart is not null
            || request.WorkEnd is not null
            || request.PrepMinutes is not null
            || request.CleanupMinutes is not null;

        if (windowChanged)
        {
            // Only a window change needs the asset to still be plannable; a title or notes edit
            // stays possible on an asset that has been closed since.
            var asset = await occupancy.ResolvePlannableAssetAsync(tenantId, assetId, cancellationToken);
            // The buffers the allocation holds today carry over unless the caller changes them.
            var window = occupancy.ValidateWindow(
                asset,
                allocation.Kind,
                request.WorkStart ?? allocation.PlannedWorkStart,
                request.WorkEnd ?? allocation.PlannedWorkEnd,
                request.PrepMinutes ?? (int)(allocation.PlannedWorkStart - allocation.OccupiedStart).TotalMinutes,
                request.CleanupMinutes ?? (int)(allocation.OccupiedEnd - allocation.PlannedWorkEnd).TotalMinutes);
            if (window.HoursViolation is { } violation)
            {
                throw PlanningException.OutsideWorkingHours(violation);
            }

            var availability = await occupancy.CheckAvailabilityAsync(
                tenantId, asset, allocation.Kind, window, allocation.Id, cancellationToken);
            if (!availability.IsFree)
            {
                throw PlanningException.Conflict(availability.Conflicts);
            }

            allocation.PlannedWorkStart = window.WorkStart;
            allocation.PlannedWorkEnd = window.WorkEnd;
            allocation.OccupiedStart = window.OccupiedStart;
            allocation.OccupiedEnd = window.OccupiedEnd;
            allocation.TimeZoneId = asset.TimeZoneId;
        }

        if (title is not null)
        {
            allocation.Title = title;
        }

        if (notesChanged)
        {
            allocation.Notes = notes;
        }

        MarkChanged(allocation);
        await SaveAsync(assetId, cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return AllocationMapper.ToResponse(
            allocation,
            await AssetNameAsync(tenantId, assetId, cancellationToken),
            timeProvider.GetUtcNow());
    }

    public async Task<AssetAllocationResponse> ApplyUsageAsync(
        Guid allocationId,
        AssetAllocationUsageRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = EnsureTenantContext();
        var action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();
        if (action is not ("start" or "work-finished" or "release" or "cancel"))
        {
            throw PlanningException.InvalidAllocation(
                "Action must be one of: start, work-finished, release, cancel.");
        }

        var expectedVersion = RequireVersion(request.ExpectedVersion);
        var reason = request.Reason?.Trim();
        if (reason is { Length: > MaxReasonLength })
        {
            throw PlanningException.InvalidAllocation($"Reason must be at most {MaxReasonLength} characters.");
        }

        var caller = await callerAccessor.GetAsync(cancellationToken);
        var assetId = await FindAssetIdAsync(tenantId, allocationId, cancellationToken);

        await using var transaction = await BeginAsync(cancellationToken);
        await PlanningModuleGuard.LockAndRequireActiveAsync(dbContext, tenantId, cancellationToken);
        await occupancy.LockAssetsAsync(tenantId, [assetId], cancellationToken);

        // Reloaded under the lock: the pre-lock read only found which asset to lock.
        var allocation = await LoadFreshAsync(tenantId, allocationId, cancellationToken);

        // Cancelling someone else's allocation is admin-only. Enforced here, not by the
        // attribute, because it depends on who created the allocation.
        if (action == "cancel"
            && !caller.CanManageAllocations
            && (caller.UserId is null || allocation.CreatedByUserId != caller.UserId))
        {
            throw new PlanningException(
                StatusCodes.Status403Forbidden,
                PlanningException.CancelForbiddenCode,
                "Only the user who created the allocation or an allocation manager can cancel it.");
        }

        EnsureManual(allocation);
        EnsureVersion(allocation, expectedVersion);

        var now = timeProvider.GetUtcNow();
        switch (action)
        {
            case "start":
                // A started allocation blocks without an end, so starting is an admission like a
                // create: re-run the check against what blocks the asset now (an overdue started
                // allocation, an early start). Invalid transitions fall through to the rules.
                if (allocation.Status == AllocationStatus.Scheduled && !AssetAllocationRules.IsExpired(allocation, now))
                {
                    var availability = await occupancy.CheckStartAsync(tenantId, allocation, cancellationToken);
                    if (!availability.IsFree)
                    {
                        throw PlanningException.Conflict(availability.Conflicts);
                    }
                }

                AssetAllocationRules.Start(allocation, now);
                break;
            case "work-finished":
                AssetAllocationRules.WorkFinished(allocation, now);
                break;
            case "release":
                AssetAllocationRules.Release(allocation, now);
                await occupancy.ReleaseAsync(allocation, cancellationToken);
                break;
            default:
                AssetAllocationRules.Cancel(allocation, now, string.IsNullOrEmpty(reason) ? null : reason);
                await occupancy.ReleaseAsync(allocation, cancellationToken);
                break;
        }

        allocation.ActorId = caller.UserId ?? allocation.ActorId;
        MarkChanged(allocation);
        await SaveAsync(assetId, cancellationToken);
        await CommitAsync(transaction, cancellationToken);

        var assetName = await AssetNameAsync(tenantId, assetId, cancellationToken);
        return AllocationMapper.ToResponse(allocation, assetName, now);
    }

    private static void EnsureMayClose(PlanningCaller caller, AssetAllocationKind kind)
    {
        // Closing an asset (an Unavailable block) is not part of using it: a technician runs
        // machines but does not take them out of service.
        if (kind == AssetAllocationKind.Unavailable && !caller.CanCloseAssets && !caller.CanManageAllocations)
        {
            throw new PlanningException(
                StatusCodes.Status403Forbidden,
                PlanningException.UnavailableForbiddenCode,
                "Creating or editing an Unavailable block needs resource-planning.availability.write or allocations.manage.");
        }
    }

    private async Task<Guid> FindAssetIdAsync(Guid tenantId, Guid allocationId, CancellationToken cancellationToken) =>
        await dbContext.AssetAllocations
            .AsNoTracking()
            .Where(a => a.Id == allocationId && a.TenantId == tenantId)
            .Select(a => (Guid?)a.AssetId)
            .FirstOrDefaultAsync(cancellationToken)
        ?? throw PlanningException.AllocationNotFound(allocationId);

    private async Task<AssetAllocation> LoadFreshAsync(
        Guid tenantId,
        Guid allocationId,
        CancellationToken cancellationToken)
    {
        var allocation = await dbContext.AssetAllocations
            .FirstOrDefaultAsync(a => a.Id == allocationId && a.TenantId == tenantId, cancellationToken)
            ?? throw PlanningException.AllocationNotFound(allocationId);

        // A tracked instance from the pre-lock read would hide a change committed while waiting.
        await dbContext.Entry(allocation).ReloadAsync(cancellationToken);
        return allocation;
    }

    private async Task<string> AssetNameAsync(Guid tenantId, Guid assetId, CancellationToken cancellationToken) =>
        await dbContext.Assets
            .AsNoTracking()
            .Where(a => a.Id == assetId && a.TenantId == tenantId)
            .Select(a => a.Name)
            .FirstOrDefaultAsync(cancellationToken)
        ?? string.Empty;

    private static void EnsureManual(AssetAllocation allocation)
    {
        // Allocations born in an order or a work order are owned by that flow (later slices).
        if (allocation.Source != AssetAllocationSource.Manual)
        {
            throw new PlanningException(
                StatusCodes.Status409Conflict,
                PlanningException.OrderWorkflowRequiredCode,
                "This allocation belongs to an order workflow; use the order endpoint.");
        }
    }

    private static void EnsureVersion(AssetAllocation allocation, string expectedVersion)
    {
        if (!string.Equals(AllocationMapper.VersionOf(allocation), expectedVersion, StringComparison.Ordinal))
        {
            throw PlanningException.VersionConflict(
                "The allocation changed since it was read; reload and retry.");
        }
    }

    private static void MarkChanged(AssetAllocation allocation)
    {
        allocation.RowVersion = Guid.NewGuid().ToByteArray();
        allocation.Touch();
    }

    private async Task SaveAsync(Guid assetId, CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (PlanningPersistenceErrors.Translate(ex, assetId) is not null)
        {
            throw PlanningPersistenceErrors.Translate(ex, assetId)!;
        }
    }

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginAsync(
        CancellationToken cancellationToken) =>
        dbContext.Database.IsRelational() && dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

    private static Task CommitAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction,
        CancellationToken cancellationToken) =>
        transaction is null ? Task.CompletedTask : transaction.CommitAsync(cancellationToken);

    private static string RequireVersion(string? expectedVersion) =>
        string.IsNullOrWhiteSpace(expectedVersion)
            ? throw PlanningException.InvalidAllocation("ExpectedVersion is required.")
            : expectedVersion.Trim();

    private static AssetAllocationKind ParseKind(string? value)
    {
        foreach (var kind in Enum.GetValues<AssetAllocationKind>())
        {
            if (string.Equals(kind.ToString(), value?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return kind;
            }
        }

        throw PlanningException.InvalidAllocation("Kind must be one of: Use, Unavailable.");
    }

    private static AllocationStatus ParseStatus(string value)
    {
        foreach (var status in Enum.GetValues<AllocationStatus>())
        {
            if (string.Equals(status.ToString(), value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return status;
            }
        }

        throw PlanningException.InvalidAllocation(
            "Status must be one of: Scheduled, Running, Cleaning, Released, Cancelled.");
    }

    private static string NormalizeTitle(string? title)
    {
        var trimmed = title?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Length > MaxTitleLength)
        {
            throw PlanningException.InvalidAllocation($"Title is required and must be at most {MaxTitleLength} characters.");
        }

        return trimmed;
    }

    private static string? NormalizeNotes(string? notes)
    {
        var trimmed = notes?.Trim();
        if (trimmed is { Length: > MaxNotesLength })
        {
            throw PlanningException.InvalidAllocation($"Notes must be at most {MaxNotesLength} characters.");
        }

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private Guid EnsureTenantContext() =>
        tenantProvider.TenantId is { } id && id != Guid.Empty
            ? id
            : throw new UnauthorizedAccessException("Tenant context is required.");
}

/// <summary>Maps an allocation to its response; the single place that derives the status flags.</summary>
internal static class AllocationMapper
{
    public static string VersionOf(AssetAllocation allocation) => Convert.ToBase64String(allocation.RowVersion);

    public static AssetAllocationResponse ToResponse(AssetAllocation a, string assetName, DateTimeOffset now) =>
        new(
            a.Id,
            a.AssetId,
            assetName,
            a.UnitId,
            a.Kind.ToString(),
            a.Title,
            a.Notes,
            a.Quantity,
            a.PlannedWorkStart,
            a.PlannedWorkEnd,
            a.OccupiedStart,
            a.OccupiedEnd,
            a.TimeZoneId,
            a.Status.ToString(),
            a.ActualStartAt,
            a.ActualWorkFinishedAt,
            a.ActualReleaseAt,
            a.Source.ToString(),
            a.ReferenceLabel,
            a.CreatedByUserId,
            a.CancelReason,
            AssetAllocationRules.IsBlocking(a, now),
            AssetAllocationRules.IsExpired(a, now),
            AssetAllocationRules.IsOverdue(a, now),
            VersionOf(a),
            a.CreatedAt);
}
