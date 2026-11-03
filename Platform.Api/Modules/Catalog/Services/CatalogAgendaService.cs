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
                .Concat(unscheduled.Where(u => u.RentalAssetId is not null).Select(u => u.RentalAssetId!.Value)),
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

        var allocated = await LoadAllocatedAsync(query, cancellationToken);
        var resourceNames = await ResolveResourceNamesAsync(
            allocated.Select(a => a.RentalAssetId), cancellationToken);

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
        // Prep/cleanup buffers extend the occupied window outward by at most one day
        // (CatalogPlanningWindow.MaxMinutes), so pad the SQL filter by a day on each side and
        // refine to the exact buffered overlap in memory below.
        var fromPad = query.From.AddDays(-1);
        var toPad = query.To.AddDays(1);

        var rows = await (
            from a in dbContext.CatalogOrderItemResourceAllocations.AsNoTracking()
            where a.IsCurrent
                && a.Status != AllocationStatus.Cancelled
                && a.PlannedWorkStart < toPad
                && a.PlannedWorkEnd > fromPad
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
                a.Status,
                a.PlannedWorkStart,
                a.PlannedWorkEnd,
                a.TimeZoneId,
                r.PrepMinutes,
                r.CleanupMinutes))
            .ToListAsync(cancellationToken);

        return rows
            .Where(r =>
            {
                var occupiedStart = r.PlannedWorkStart.AddMinutes(-r.PrepMinutes);
                var occupiedEnd = r.PlannedWorkEnd.AddMinutes(r.CleanupMinutes);
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
                o.CreatedAt))
            .ToListAsync(cancellationToken);

    private async Task<IReadOnlyDictionary<Guid, string>> ResolveResourceNamesAsync(
        IEnumerable<Guid> rentalAssetIds,
        CancellationToken cancellationToken)
    {
        var ids = rentalAssetIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        return await (
            from ra in dbContext.RentalAssets.AsNoTracking()
            where ids.Contains(ra.Id)
            join asset in dbContext.Assets.AsNoTracking() on ra.AssetId equals asset.Id
            select new { ra.Id, asset.Name })
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
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
        IReadOnlyDictionary<Guid, string> resourceNames,
        bool includeOrderDetails) =>
        new(
            r.OrderId,
            includeOrderDetails ? $"#{r.OrderNumber}" : null,
            r.OrderStatus,
            r.ProductId,
            r.ProductName,
            r.RentalAssetId,
            resourceNames.GetValueOrDefault(r.RentalAssetId),
            includeOrderDetails ? r.CustomerName : null,
            r.PlannedWorkStart,
            r.PlannedWorkEnd,
            r.PlannedWorkStart.AddMinutes(-r.PrepMinutes),
            r.PlannedWorkEnd.AddMinutes(r.CleanupMinutes),
            r.AllocationStatus,
            r.TimeZoneId);

    private static CatalogAgendaItemResponse MapUnscheduled(
        UnscheduledRow r,
        IReadOnlyDictionary<Guid, string> resourceNames,
        string timeZoneId) =>
        new(
            r.OrderId,
            $"#{r.OrderNumber}",
            r.OrderStatus,
            r.ProductId,
            r.ProductName,
            r.RentalAssetId,
            r.RentalAssetId is { } assetId ? resourceNames.GetValueOrDefault(assetId) : null,
            r.CustomerName,
            WorkStart: null,
            WorkEnd: null,
            // No planned window yet: the request time stands in as the agenda anchor so the row
            // sorts alongside allocations and renders as "requested, not yet scheduled".
            r.RequestedAt,
            r.RequestedAt,
            AllocationStatus: null,
            timeZoneId);

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
        Guid RentalAssetId,
        AllocationStatus AllocationStatus,
        DateTimeOffset PlannedWorkStart,
        DateTimeOffset PlannedWorkEnd,
        string TimeZoneId,
        int PrepMinutes,
        int CleanupMinutes);

    private sealed record UnscheduledRow(
        Guid OrderId,
        int OrderNumber,
        CatalogOrderStatus OrderStatus,
        string CustomerName,
        Guid ProductId,
        string ProductName,
        Guid? RentalAssetId,
        DateTimeOffset RequestedAt);
}
