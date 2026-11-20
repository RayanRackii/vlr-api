using Microsoft.EntityFrameworkCore;
using Platform.Api.Authorization;
using Platform.Api.Modules.Rentals.Contracts;
using Platform.Api.Modules.ResourcePlanning;
using Platform.Api.Modules.ResourcePlanning.Contracts;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Catalog.Services;

/// <summary>
/// A resource-planning rule the order flow reports (for example the asset's working hours). The
/// controller returns <see cref="StatusCode"/> with <c>{ error, code }</c> (plus
/// <c>hoursViolation</c> when set), the same shape the planning endpoints use.
/// </summary>
public sealed class CatalogPlanningRuleException(
    int statusCode,
    string code,
    string message,
    string? hoursViolation = null) : InvalidOperationException(message)
{
    public int StatusCode { get; } = statusCode;

    public string Code { get; } = code;

    public string? HoursViolation { get; } = hoursViolation;
}

/// <summary>
/// A module the allocation flow needs is not active for the tenant. Typed so the controller answers
/// 403 <c>module_inactive</c>, the body the module gate returns for the same situation, instead of a
/// 500 from a bare <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class RequiredModuleInactiveException(IReadOnlyList<string> modules)
    : InvalidOperationException(
        $"Cannot allocate a resource: module(s) {string.Join(", ", modules)} are not active for this tenant.")
{
    public IReadOnlyList<string> Modules { get; } = modules;
}

/// <summary>
/// The order flow's side of the planning port: resolving the asset a requirement points at,
/// the duration limit per kind of asset, and translating planning rule violations into the exceptions
/// the catalog controllers already map. The only catalog file that names the planning exception.
/// </summary>
internal static class CatalogOrderPlanner
{
    public const string ResourceNotFound = "The configured resource was not found for this tenant.";

    /// <summary>
    /// The asset a requirement targets: its <c>AssetId</c>, else the asset of its rental asset (rows
    /// written before slice API-5). <see cref="Guid.Empty"/> when neither resolves, which the port then
    /// reports as not found (and which locks nothing).
    /// </summary>
    public static async Task<Guid> ResolveAssetIdAsync(
        AppDbContext db,
        Guid tenantId,
        Guid? assetId,
        Guid? rentalAssetId,
        CancellationToken cancellationToken)
    {
        if (assetId is { } direct)
        {
            return direct;
        }

        if (rentalAssetId is not { } rental)
        {
            return Guid.Empty;
        }

        return await db.RentalAssets
            .AsNoTracking()
            .Where(r => r.Id == rental && r.TenantId == tenantId)
            .Select(r => r.AssetId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// The assets of existing catalog allocations, from the planning record when linked and from the
    /// rental asset otherwise. Used to lock the asset rows before an allocation is reloaded.
    /// </summary>
    public static async Task<List<Guid>> ResolveAllocationAssetIdsAsync(
        AppDbContext db,
        Guid tenantId,
        IReadOnlyCollection<(Guid? AssetAllocationId, Guid? RentalAssetId)> links,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<Guid>();

        var planningIds = links.Where(l => l.AssetAllocationId != null).Select(l => l.AssetAllocationId!.Value).Distinct().ToList();
        if (planningIds.Count > 0)
        {
            result.UnionWith(await db.AssetAllocations
                .AsNoTracking()
                .Where(a => a.TenantId == tenantId && planningIds.Contains(a.Id))
                .Select(a => a.AssetId)
                .ToListAsync(cancellationToken));
        }

        var rentalIds = links
            .Where(l => l.AssetAllocationId == null && l.RentalAssetId != null)
            .Select(l => l.RentalAssetId!.Value)
            .Distinct()
            .ToList();
        if (rentalIds.Count > 0)
        {
            result.UnionWith(await db.RentalAssets
                .AsNoTracking()
                .Where(r => r.TenantId == tenantId && rentalIds.Contains(r.Id))
                .Select(r => r.AssetId)
                .ToListAsync(cancellationToken));
        }

        return [.. result];
    }

    /// <summary>
    /// Resolves the target asset through the port. Not found becomes <see cref="KeyNotFoundException"/>
    /// and an asset that cannot be planned becomes <see cref="ResourceUnavailableException"/>, the
    /// exceptions the Phase 0 flow raised. A rental asset the rentals rules call unavailable keeps the
    /// rentals wording.
    /// </summary>
    public static async Task<PlannableAsset> ResolveAsync(
        IAssetOccupancyService port,
        IResourceOccupancyService rentals,
        AppDbContext db,
        Guid tenantId,
        Guid assetId,
        Guid? requirementRentalAssetId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await port.ResolveOrderAssetAsync(tenantId, assetId, cancellationToken);
        }
        catch (PlanningException ex) when (ex.Code == PlanningException.AssetNotFoundCode)
        {
            throw new KeyNotFoundException(ResourceNotFound);
        }
        catch (PlanningException ex) when (ex.Code == PlanningException.AssetNotPlannableCode)
        {
            if (requirementRentalAssetId is { } rentalAssetId
                && await rentals.ResolveEligibleResourceAsync(db, tenantId, rentalAssetId, cancellationToken)
                    is { IsEligible: false } ineligible)
            {
                throw new ResourceUnavailableException(
                    rentalAssetId,
                    ineligible.IneligibilityReason
                        ?? "The configured resource is not currently available for production use.");
            }

            throw new ResourceUnavailableException(assetId, ex.Message);
        }
    }

    /// <summary>Longest work window of one allocation: a day on a rental-backed asset, 30 days otherwise.</summary>
    public static int MaxWorkMinutes(PlannableAsset asset) =>
        asset.IsRentalBacked ? CatalogPlanningWindow.MaxMinutes : AssetPlanningLimits.MaxPlanningOnlyDurationMinutes;

    /// <summary>
    /// A planning-only asset is stamped with the unit the staff chose, so that unit must exist in the
    /// tenant and be the asset's own, the check the rentals port makes for a rental-backed asset.
    /// </summary>
    public static async Task EnsureUnitAsync(
        AppDbContext db,
        Guid tenantId,
        Guid unitId,
        PlannableAsset asset,
        CancellationToken cancellationToken)
    {
        var exists = await db.Units
            .AsNoTracking()
            .AnyAsync(u => u.Id == unitId && u.TenantId == tenantId, cancellationToken);
        if (!exists)
        {
            throw new KeyNotFoundException($"Unit '{unitId}' was not found.");
        }

        if (asset.UnitId != unitId)
        {
            throw new KeyNotFoundException("Resource not found for this unit.");
        }
    }

    /// <summary>Raises the window problem the port reported, if any, in the catalog's own terms.</summary>
    public static void ThrowIfRejected(AllocationWindow window)
    {
        if (window.RentalWindowError is { } rentalError)
        {
            throw new UnsupportedWorkWindowException(rentalError);
        }

        if (window.HoursViolation is { } violation)
        {
            throw Translate(PlanningException.OutsideWorkingHours(violation), asset: null);
        }
    }

    public static string ReferenceLabel(CatalogOrder order) => $"Pedido #{order.OrderNumber}";

    public static string Title(CatalogOrder order, CatalogOrderItem item)
    {
        var title = $"{ReferenceLabel(order)} - {item.ProductNameSnapshot}";
        return title.Length <= 200 ? title : title[..200];
    }

    /// <summary>Runs a port call and turns a planning violation into the catalog's exceptions.</summary>
    public static async Task<T> RunAsync<T>(PlannableAsset asset, Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (PlanningException ex)
        {
            throw Translate(ex, asset);
        }
    }

    private static Exception Translate(PlanningException ex, PlannableAsset? asset) =>
        ex.Code switch
        {
            PlanningException.ConflictCode => new ResourceUnavailableException(
                asset?.RentalAssetId ?? asset?.AssetId ?? Guid.Empty,
                asset is null
                    ? "The configured resource is already booked for an overlapping interval."
                    : $"'{asset.Name}' is already booked for an overlapping interval."),
            PlanningException.InvalidAllocationCode => new ArgumentException(ex.Message),
            _ => new CatalogPlanningRuleException(ex.StatusCode, ex.Code, ex.Message, ex.HoursViolation),
        };
}

/// <summary>
/// Keeps a catalog allocation row and its planning record in step (the catalog columns stay the
/// lifecycle of record for the order flow during phase 1) and frees the occupancy.
/// </summary>
internal static class CatalogAllocationMirror
{
    /// <summary>Copies the lifecycle of the catalog row onto its planning record, when it has one.</summary>
    public static void Apply(CatalogOrderItemResourceAllocation source, AssetAllocation? target)
    {
        if (target is null)
        {
            return;
        }

        target.Status = source.Status;
        target.ActualStartAt = source.ActualStartAt;
        target.ActualWorkFinishedAt = source.ActualWorkFinishedAt;
        target.ActualReleaseAt = source.ActualReleaseAt;
        target.RowVersion = Guid.NewGuid().ToByteArray();
        target.Touch();
    }

    /// <summary>
    /// Frees the occupancy of a cancelled or released allocation through the port (which frees the
    /// mirror reservation of a rental-backed asset). A row not linked to a planning record falls back to
    /// the rentals port directly, as before the link existed. Call after <see cref="Apply"/>: the
    /// port reads the actuals of the planning record.
    /// </summary>
    public static async Task ReleaseOccupancyAsync(
        IAssetOccupancyService port,
        IResourceOccupancyService rentals,
        AppDbContext db,
        Guid tenantId,
        CatalogOrderItemResourceAllocation allocation,
        AssetAllocation? planning,
        InternalUseReleaseKind legacyKind,
        CancellationToken cancellationToken)
    {
        if (planning is not null)
        {
            await port.ReleaseAsync(planning, cancellationToken);
            return;
        }

        if (allocation.ReservationItemId is { } reservationItemId)
        {
            await rentals.ReleaseInternalUseOccupancyAsync(
                db, tenantId, reservationItemId, legacyKind, allocation.PlannedWorkEnd, cancellationToken);
        }
    }

    /// <summary>
    /// Loads the planning records of the given allocations (tracked) and reloads them, so the caller sees
    /// the committed state once it holds the asset lock.
    /// </summary>
    public static async Task<Dictionary<Guid, AssetAllocation>> LoadPlanningAsync(
        AppDbContext db,
        Guid tenantId,
        IEnumerable<CatalogOrderItemResourceAllocation> allocations,
        CancellationToken cancellationToken)
    {
        var ids = allocations
            .Where(a => a.AssetAllocationId != null)
            .Select(a => a.AssetAllocationId!.Value)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, AssetAllocation>();
        }

        var planning = await db.AssetAllocations
            .Where(a => a.TenantId == tenantId && ids.Contains(a.Id))
            .ToListAsync(cancellationToken);
        foreach (var record in planning)
        {
            await db.Entry(record).ReloadAsync(cancellationToken);
        }

        return planning.ToDictionary(a => a.Id);
    }
}
