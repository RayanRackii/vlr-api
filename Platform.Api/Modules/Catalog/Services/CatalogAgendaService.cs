using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Services.Tenancy;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Catalog.Services;

public interface ICatalogAgendaService
{
    /// <summary>
    /// Product-centric agenda for <c>[from, to)</c>: one row per current, non-cancelled
    /// allocation whose buffered window overlaps the range, plus every still-unscheduled
    /// Requested/Approved line whose order was requested inside the range.
    /// </summary>
    Task<IReadOnlyList<CatalogAgendaItemResponse>> GetProductsAgendaAsync(
        CatalogAgendaQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resource-centric agenda for <c>[from, to)</c>: only allocated rows (no unscheduled lines),
    /// plus the list of resources so the caller can render empty rows. When
    /// <paramref name="includeOrderDetails"/> is false the customer name and display number are
    /// withheld (the caller lacks catalog.orders.read).
    /// </summary>
    Task<CatalogAgendaResourcesResponse> GetResourcesAgendaAsync(
        CatalogAgendaQuery query,
        bool includeOrderDetails,
        CancellationToken cancellationToken);
}

public sealed class CatalogAgendaService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider) : ICatalogAgendaService
{
    public async Task<IReadOnlyList<CatalogAgendaItemResponse>> GetProductsAgendaAsync(
        CatalogAgendaQuery query,
        CancellationToken cancellationToken)
    {
        EnsureTenant();

        var allocated = await LoadAllocatedAsync(query, cancellationToken);
        var unscheduled = await LoadUnscheduledAsync(query, cancellationToken);

        var resourceNames = await ResolveResourceNamesAsync(
            allocated.Select(a => a.RentalAssetId)
                .Concat(unscheduled.Select(u => u.RentalAssetId)),
            allocated.Select(a => a.AssetId)
                .Concat(unscheduled.Select(u => u.AssetId)),
            cancellationToken);

        var fallbackZone = unscheduled.Count == 0
            ? null
            : await TenantTimeZoneProvider.ResolveIanaIdForTenantAsync(dbContext, EnsureTenant(), cancellationToken);

        var items = allocated
            .Select(a => MapAllocated(a, resourceNames, includeOrderDetails: true))
            .Concat(unscheduled.Select(u => MapUnscheduled(u, resourceNames, fallbackZone!)))
            .OrderBy(i => i.OccupiedStart)
            .ToList();

        return items;
    }

    public async Task<CatalogAgendaResourcesResponse> GetResourcesAgendaAsync(
        CatalogAgendaQuery query,
        bool includeOrderDetails,
        CancellationToken cancellationToken)
    {
        EnsureTenant();

        // The resource agenda is the rentals one: rows are keyed by rental asset, so an allocation on
        // an asset with no rental configuration is not on it (the planning agenda draws it).
        var allocated = (await LoadAllocatedAsync(query, cancellationToken))
            .Where(a => a.RentalAssetId != null)
            .ToList();
        var resourceNames = await ResolveResourceNamesAsync(
            allocated.Select(a => a.RentalAssetId), [], cancellationToken);

        var items = allocated
            .Select(a => MapAllocated(a, resourceNames, includeOrderDetails))
            .OrderBy(i => i.OccupiedStart)
            .ToList();

        var resources = await LoadResourcesAsync(query.RentalAssetId, cancellationToken);

        return new CatalogAgendaResourcesResponse(items, resources);
    }

    private async Task<List<AllocatedRow>> LoadAllocatedAsync(
        CatalogAgendaQuery query,
        CancellationToken cancellationToken)
    {
        // An allocation linked to its planning record reads the stored occupied window (work plus
        // prep and cleanup, written once at allocation time), so the range filter is exact in SQL.
        // A row not linked yet (before the Phase 0 backfill ran) still derives the window from the
        // requirement: prep/cleanup buffers extend it outward by at most one day
        // (CatalogPlanningWindow.MaxMinutes), so that filter is padded by a day on each side and
        // refined to the exact buffered overlap in memory below.
        var fromPad = query.From.AddDays(-1);
        var toPad = query.To.AddDays(1);

        var rows = await (
            from a in dbContext.CatalogOrderItemResourceAllocations.AsNoTracking()
            where a.IsCurrent
                && a.Status != AllocationStatus.Cancelled
            join planning in dbContext.AssetAllocations.AsNoTracking()
                on a.AssetAllocationId equals (Guid?)planning.Id into planningLinks
            from planning in planningLinks.DefaultIfEmpty()
            where planning != null
                ? planning.OccupiedStart < query.To && planning.OccupiedEnd > query.From
                : a.PlannedWorkStart < toPad && a.PlannedWorkEnd > fromPad
            join r in dbContext.CatalogOrderItemResourceRequirements.AsNoTracking()
                on a.RequirementId equals r.Id
            join i in dbContext.CatalogOrderItems.AsNoTracking()
                on r.OrderItemId equals i.Id
            join o in dbContext.CatalogOrders.AsNoTracking()
                on i.OrderId equals o.Id
            where query.ProductId == null || i.ProductId == query.ProductId
            where query.RentalAssetId == null || a.RentalAssetId == query.RentalAssetId
            select new AllocatedRow(
                o.Id,
                o.OrderNumber,
                o.Status,
                o.CustomerNameSnapshot,
                i.ProductId,
                i.ProductNameSnapshot,
                a.RentalAssetId,
                planning == null ? r.AssetId : (Guid?)planning.AssetId,
                a.Status,
                a.PlannedWorkStart,
                a.PlannedWorkEnd,
                a.TimeZoneId,
                r.PrepMinutes,
                r.CleanupMinutes,
                planning == null ? null : (DateTimeOffset?)planning.OccupiedStart,
                planning == null ? null : (DateTimeOffset?)planning.OccupiedEnd))
            .ToListAsync(cancellationToken);

        return rows
            .Where(r =>
            {
                var occupiedStart = r.StoredOccupiedStart ?? r.PlannedWorkStart.AddMinutes(-r.PrepMinutes);
                var occupiedEnd = r.StoredOccupiedEnd ?? r.PlannedWorkEnd.AddMinutes(r.CleanupMinutes);
                return occupiedStart < query.To && occupiedEnd > query.From;
            })
            .ToList();
    }

    private async Task<List<UnscheduledRow>> LoadUnscheduledAsync(
        CatalogAgendaQuery query,
        CancellationToken cancellationToken) =>
        await (
            from i in dbContext.CatalogOrderItems.AsNoTracking()
            join r in dbContext.CatalogOrderItemResourceRequirements.AsNoTracking()
                on i.Id equals r.OrderItemId
            join o in dbContext.CatalogOrders.AsNoTracking()
                on i.OrderId equals o.Id
            where (o.Status == CatalogOrderStatus.Requested || o.Status == CatalogOrderStatus.Approved)
                && o.CreatedAt >= query.From
                && o.CreatedAt < query.To
                && (query.ProductId == null || i.ProductId == query.ProductId)
                && !dbContext.CatalogOrderItemResourceAllocations
                    .Any(a => a.RequirementId == r.Id && a.IsCurrent && a.Status != AllocationStatus.Cancelled)
            select new UnscheduledRow(
                o.Id,
                o.OrderNumber,
                o.Status,
                o.CustomerNameSnapshot,
                i.ProductId,
                i.ProductNameSnapshot,
                r.RentalAssetId,
                r.AssetId,
                o.CreatedAt))
            .ToListAsync(cancellationToken);

    /// <summary>Display names by rental asset id and by asset id, in two batched queries at most.</summary>
    private async Task<ResourceNames> ResolveResourceNamesAsync(
        IEnumerable<Guid?> rentalAssetIds,
        IEnumerable<Guid?> assetIds,
        CancellationToken cancellationToken)
    {
        var rentalIds = rentalAssetIds.OfType<Guid>().Distinct().ToList();
        var plainIds = assetIds.OfType<Guid>().Distinct().ToList();

        var byRental = rentalIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await (
                from ra in dbContext.RentalAssets.AsNoTracking()
                where rentalIds.Contains(ra.Id)
                join asset in dbContext.Assets.AsNoTracking() on ra.AssetId equals asset.Id
                select new { ra.Id, asset.Name })
                .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);

        var byAsset = plainIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await dbContext.Assets.AsNoTracking()
                .Where(a => plainIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, a => a.Name, cancellationToken);

        return new ResourceNames(byRental, byAsset);
    }

    private async Task<IReadOnlyList<CatalogAgendaResourceResponse>> LoadResourcesAsync(
        Guid? rentalAssetId,
        CancellationToken cancellationToken) =>
        await (
            from ra in dbContext.RentalAssets.AsNoTracking()
            where rentalAssetId == null || ra.Id == rentalAssetId
            join asset in dbContext.Assets.AsNoTracking() on ra.AssetId equals asset.Id
            join category in dbContext.AssetCategories.AsNoTracking() on asset.CategoryId equals category.Id
                into cat
            from category in cat.DefaultIfEmpty()
            orderby asset.Name
            select new CatalogAgendaResourceResponse(
                ra.Id,
                asset.Name,
                category == null ? null : category.Name))
            .ToListAsync(cancellationToken);

    private static CatalogAgendaItemResponse MapAllocated(
        AllocatedRow r,
        ResourceNames resourceNames,
        bool includeOrderDetails) =>
        new(
            r.OrderId,
            includeOrderDetails ? $"#{r.OrderNumber}" : null,
            r.OrderStatus,
            r.ProductId,
            r.ProductName,
            r.RentalAssetId,
            resourceNames.NameOf(r.RentalAssetId, r.AssetId),
            includeOrderDetails ? r.CustomerName : null,
            r.PlannedWorkStart,
            r.PlannedWorkEnd,
            r.StoredOccupiedStart ?? r.PlannedWorkStart.AddMinutes(-r.PrepMinutes),
            r.StoredOccupiedEnd ?? r.PlannedWorkEnd.AddMinutes(r.CleanupMinutes),
            r.AllocationStatus,
            r.TimeZoneId,
            r.AssetId);

    private static CatalogAgendaItemResponse MapUnscheduled(
        UnscheduledRow r,
        ResourceNames resourceNames,
        string timeZoneId) =>
        new(
            r.OrderId,
            $"#{r.OrderNumber}",
            r.OrderStatus,
            r.ProductId,
            r.ProductName,
            r.RentalAssetId,
            resourceNames.NameOf(r.RentalAssetId, r.AssetId),
            r.CustomerName,
            WorkStart: null,
            WorkEnd: null,
            // No planned window yet: the request time stands in as the agenda anchor so the row
            // sorts alongside allocations and renders as "requested, not yet scheduled".
            r.RequestedAt,
            r.RequestedAt,
            AllocationStatus: null,
            timeZoneId,
            r.AssetId);

    private Guid EnsureTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");

    private sealed record AllocatedRow(
        Guid OrderId,
        int OrderNumber,
        CatalogOrderStatus OrderStatus,
        string CustomerName,
        Guid ProductId,
        string ProductName,
        Guid? RentalAssetId,
        Guid? AssetId,
        AllocationStatus AllocationStatus,
        DateTimeOffset PlannedWorkStart,
        DateTimeOffset PlannedWorkEnd,
        string TimeZoneId,
        int PrepMinutes,
        int CleanupMinutes,
        DateTimeOffset? StoredOccupiedStart,
        DateTimeOffset? StoredOccupiedEnd);

    private sealed record UnscheduledRow(
        Guid OrderId,
        int OrderNumber,
        CatalogOrderStatus OrderStatus,
        string CustomerName,
        Guid ProductId,
        string ProductName,
        Guid? RentalAssetId,
        Guid? AssetId,
        DateTimeOffset RequestedAt);

    /// <summary>Display names of the resources on an agenda, by rental asset id and by asset id.</summary>
    private sealed record ResourceNames(
        IReadOnlyDictionary<Guid, string> ByRental,
        IReadOnlyDictionary<Guid, string> ByAsset)
    {
        public string? NameOf(Guid? rentalAssetId, Guid? assetId) =>
            rentalAssetId is { } rental && ByRental.TryGetValue(rental, out var rentalName) ? rentalName
            : assetId is { } asset && ByAsset.TryGetValue(asset, out var assetName) ? assetName
            : null;
    }
}
