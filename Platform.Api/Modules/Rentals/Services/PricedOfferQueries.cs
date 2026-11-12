using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Contracts;
using Platform.Core.Domain.Constants;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>
/// A priced offer reaches a resource when direct rental has a positive price,
/// or an active catalog product with a positive price requires that resource.
/// Tenant filters stay on: this runs inside the caller's tenant.
/// </summary>
internal static class PricedOfferQueries
{
    public static Task<bool> IsOnMenuAsync(
        AppDbContext db,
        Guid tenantId,
        Guid rentalAssetId,
        CancellationToken cancellationToken) =>
        db.TenantModuleMenuItems.AsNoTracking().AnyAsync(
            item => item.TenantId == tenantId
                && item.IsActive
                && item.ModuleName == PlatformModules.Rentals
                && (item.RentalAssetId == null || item.RentalAssetId == rentalAssetId),
            cancellationToken);

    public static Task<bool> CatalogReachesAsync(
        AppDbContext db,
        Guid tenantId,
        Guid rentalAssetId,
        CancellationToken cancellationToken) =>
        (
            from requirement in db.CatalogProductResourceRequirements.AsNoTracking()
            join product in db.CatalogProducts.AsNoTracking()
                on requirement.ProductId equals product.Id
            where requirement.TenantId == tenantId
                && product.TenantId == tenantId
                && requirement.RentalAssetId == rentalAssetId
                && requirement.IsActive
                && product.IsActive
                && product.Price > 0
            select requirement.Id).AnyAsync(cancellationToken);
}
