using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals;
using Platform.Core.Domain.Constants;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>
/// A priced offer reaches a resource when direct rental has a positive price,
/// or an active catalog product with a positive price requires that resource.
/// </summary>
internal static class PricedOfferQueries
{
    public static async Task EnsureCanPublishAsync(
        AppDbContext db,
        Guid tenantId,
        string moduleName,
        Guid? rentalAssetId,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(moduleName, PlatformModules.Rentals, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var ids = rentalAssetId is Guid id
            ? [id]
            : await ActiveResourceIdsAsync(db, tenantId, cancellationToken);

        foreach (var resourceId in ids)
        {
            if (!await ReachesAsync(db, tenantId, resourceId, cancellationToken))
            {
                throw new PricedOfferRequiredException(PricedOfferRequiredException.PublishMessage);
            }
        }
    }

    public static Task<bool> IsOnMenuAsync(
        AppDbContext db,
        Guid tenantId,
        Guid rentalAssetId,
        CancellationToken cancellationToken) =>
        db.TenantModuleMenuItems.IgnoreQueryFilters().AsNoTracking().AnyAsync(
            item => item.TenantId == tenantId
                && item.IsActive
                && item.ModuleName == PlatformModules.Rentals
                && (item.RentalAssetId == null || item.RentalAssetId == rentalAssetId),
            cancellationToken);

    public static async Task<bool> ReachesAsync(
        AppDbContext db,
        Guid tenantId,
        Guid rentalAssetId,
        CancellationToken cancellationToken)
    {
        var direct = await db.RentalAssets.IgnoreQueryFilters().AsNoTracking()
            .Where(asset => asset.TenantId == tenantId && asset.Id == rentalAssetId)
            .Select(asset => (bool?)asset.DirectRental)
            .FirstOrDefaultAsync(cancellationToken);

        if (direct is null)
        {
            return false;
        }

        if (direct.Value && await HasPositiveResourcePriceAsync(db, tenantId, rentalAssetId, cancellationToken))
        {
            return true;
        }

        return await CatalogReachesAsync(db, tenantId, rentalAssetId, cancellationToken);
    }

    public static Task<bool> CatalogReachesAsync(
        AppDbContext db,
        Guid tenantId,
        Guid rentalAssetId,
        CancellationToken cancellationToken) =>
        (
            from requirement in db.CatalogProductResourceRequirements.IgnoreQueryFilters().AsNoTracking()
            join product in db.CatalogProducts.IgnoreQueryFilters().AsNoTracking()
                on requirement.ProductId equals product.Id
            where requirement.TenantId == tenantId
                && product.TenantId == tenantId
                && requirement.RentalAssetId == rentalAssetId
                && requirement.IsActive
                && product.IsActive
                && product.Price > 0
            select requirement.Id).AnyAsync(cancellationToken);

    private static Task<bool> HasPositiveResourcePriceAsync(
        AppDbContext db,
        Guid tenantId,
        Guid rentalAssetId,
        CancellationToken cancellationToken) =>
        db.RentalPricings.IgnoreQueryFilters().AsNoTracking().AnyAsync(
            price => price.TenantId == tenantId
                && price.RentalAssetId == rentalAssetId
                && price.PricePerHour > 0,
            cancellationToken);

    private static Task<List<Guid>> ActiveResourceIdsAsync(
        AppDbContext db,
        Guid tenantId,
        CancellationToken cancellationToken) =>
        db.RentalAssets.IgnoreQueryFilters().AsNoTracking()
            .Where(asset => asset.TenantId == tenantId && asset.IsActive)
            .Select(asset => asset.Id)
            .ToListAsync(cancellationToken);
}
