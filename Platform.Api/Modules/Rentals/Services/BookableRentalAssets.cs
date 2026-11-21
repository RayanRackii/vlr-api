using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>
/// The single definition of a bookable rental asset: rental active, asset rentable, status Active and
/// not scheduled for deletion. Listing, availability and reservation creation all use it so a
/// soft-deleted, Inactive or Maintenance asset is never offered nor accepted.
/// </summary>
internal static class BookableRentalAssets
{
    public static IQueryable<RentalAsset> WhereBookable(this IQueryable<RentalAsset> query) =>
        query.Where(r => r.IsActive
            && r.Asset.IsRentable
            && r.Asset.Status == AssetStatus.Active
            && r.Asset.ScheduledDeletionAt == null);
}
