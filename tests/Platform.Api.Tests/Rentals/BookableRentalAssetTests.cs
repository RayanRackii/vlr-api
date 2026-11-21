using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Services;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Rentals;

public sealed class BookableRentalAssetTests
{
    public enum Blocker
    {
        SoftDeleted,
        Inactive,
        Maintenance,
    }

    private static async Task ApplyAsync(TwoLocationBookingHarness harness, Guid assetId, Blocker blocker)
    {
        var asset = await harness.Db.Assets.SingleAsync(a => a.Id == assetId);
        switch (blocker)
        {
            case Blocker.SoftDeleted:
                asset.ScheduledDeletionAt = DateTimeOffset.UtcNow.AddDays(30);
                break;
            case Blocker.Inactive:
                asset.Status = AssetStatus.Inactive;
                break;
            case Blocker.Maintenance:
                asset.Status = AssetStatus.Maintenance;
                break;
        }

        await harness.Db.SaveChangesAsync();
    }

    [Theory]
    [InlineData(Blocker.SoftDeleted)]
    [InlineData(Blocker.Inactive)]
    [InlineData(Blocker.Maintenance)]
    public async Task Reservation_is_refused_for_a_non_bookable_asset(Blocker blocker)
    {
        await using var harness = await TwoLocationBookingHarness.CreateAsync();
        await ApplyAsync(harness, harness.AssetIds[0], blocker);
        var service = harness.CreateReservationService();

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.CreateReservationAsync(
                harness.CustomerId,
                harness.CreateRequest(harness.AssetIds[0]),
                CancellationToken.None));

        Assert.Equal(0, await harness.Db.Reservations.CountAsync());
    }

    [Theory]
    [InlineData(Blocker.SoftDeleted)]
    [InlineData(Blocker.Inactive)]
    [InlineData(Blocker.Maintenance)]
    public async Task Customer_listing_does_not_offer_a_non_bookable_asset(Blocker blocker)
    {
        await using var harness = await TwoLocationBookingHarness.CreateAsync();
        await ApplyAsync(harness, harness.AssetIds[0], blocker);
        var rentals = new RentalAssetService(harness.Db, harness.TenantProvider);

        var bookable = await rentals.ListBookableAsync(CancellationToken.None);

        Assert.DoesNotContain(bookable, r => r.AssetId == harness.AssetIds[0]);
        Assert.Contains(bookable, r => r.AssetId == harness.AssetIds[1]);
    }

    [Fact]
    public async Task Staff_listing_still_shows_a_maintenance_asset()
    {
        await using var harness = await TwoLocationBookingHarness.CreateAsync();
        await ApplyAsync(harness, harness.AssetIds[0], Blocker.Maintenance);
        var rentals = new RentalAssetService(harness.Db, harness.TenantProvider);

        var all = await rentals.ListRentableAsync(CancellationToken.None);

        Assert.Contains(all, r => r.AssetId == harness.AssetIds[0]);
    }
}
