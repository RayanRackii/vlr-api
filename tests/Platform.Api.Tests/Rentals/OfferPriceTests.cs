using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.ModuleMenuItems.Dtos;
using Platform.Api.Modules.ModuleMenuItems.Services;
using Platform.Api.Modules.Rentals;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Contracts.ResourceVisuals;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Rentals;

public sealed class OfferPriceTests
{
    [Fact]
    public void Derived_window_ignores_a_zero_price()
    {
        var zero = Price(0m);
        var paid = Price(40m);

        Assert.Same(paid, BookingOffer.ForDerivedWindow([zero, paid], 1, 1));
        Assert.Null(BookingOffer.ForDerivedWindow([zero], 1, 1));
        Assert.Null(BookingOffer.PositivePrice(0m));
        Assert.Equal(40m, BookingOffer.PositivePrice(40m));
    }

    [Fact]
    public void Default_pricing_rejects_zero()
    {
        var row = new CreateRentalPricingDto
        {
            DayOfWeek = DayOfWeek.Monday,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(9, 0),
            PricePerHour = 0m,
        };

        var error = Assert.Throws<ArgumentException>(() => TemplateDefaultPricing.Validate([row]));
        Assert.Contains("greater than zero", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Zero_price_is_not_quoted_or_booked()
    {
        await using var h = await ReservationQueueHarness.CreateAsync(queueEnabled: false, includeSlot: true);
        var priced = await h.Reservations().CheckAvailabilityAsync(Request(h), CancellationToken.None);
        Assert.True(priced.IsAvailable);
        Assert.Equal(100m, priced.EstimatedTotalAmount);

        (await h.Db.RentalPricings.SingleAsync()).PricePerHour = 0m;
        await h.Db.SaveChangesAsync();

        var quote = await h.Reservations().CheckAvailabilityAsync(Request(h), CancellationToken.None);
        Assert.False(quote.IsAvailable);
        Assert.Null(quote.EstimatedTotalAmount);

        var day = await new DailyAvailabilityService(h.Db, h.TenantProvider, h.Time).GetAsync(
            ReservationQueueHarness.BookDate, [h.RentalAssetId], h.UnitId, 1, 60, CancellationToken.None);
        Assert.All(day.Resources.Single().Windows, window => Assert.Null(window.TotalAmount));
        Assert.Contains(day.Resources.Single().Windows, window => window.State == AvailabilityWindowState.Unpriced);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            h.Reservations().CreateReservationAsync(h.CustomerA, h.BookRequest(), CancellationToken.None));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            h.Schedule().BookSlotAsync(h.CustomerA, new BookSlotRequestDto
            {
                SlotId = h.SlotId!.Value,
                UnitId = h.UnitId,
            }, CancellationToken.None));
        Assert.Empty(await h.Db.Reservations.ToListAsync());

        var pricing = new RentalPricingService(h.Db, h.TenantProvider);
        var rejected = await Assert.ThrowsAsync<ArgumentException>(() => pricing.CreateAsync(h.AssetId, new CreateRentalPricingDto
        {
            DayOfWeek = ReservationQueueHarness.BookDate.DayOfWeek,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(9, 0),
            PricePerHour = 0m,
        }, CancellationToken.None));
        Assert.Contains("greater than zero", rejected.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Direct_rental_off_does_not_quote_the_resource_price()
    {
        await using var h = await ReservationQueueHarness.CreateAsync(queueEnabled: false, includeSlot: true);
        (await h.Db.RentalAssets.SingleAsync()).DirectRental = false;
        await h.Db.SaveChangesAsync();

        var quote = await h.Reservations().CheckAvailabilityAsync(Request(h), CancellationToken.None);
        Assert.False(quote.IsAvailable);
        Assert.Null(quote.EstimatedTotalAmount);
        Assert.Equal(BookingQuote.NotDirectRental, quote.Reason);

        var day = await new DailyAvailabilityService(h.Db, h.TenantProvider, h.Time).GetAsync(
            ReservationQueueHarness.BookDate, [h.RentalAssetId], h.UnitId, 1, 60, CancellationToken.None);
        Assert.All(day.Resources.Single().Windows, window => Assert.Null(window.TotalAmount));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Reservations().CreateReservationAsync(h.CustomerA, h.BookRequest(), CancellationToken.None));
        Assert.Equal(BookingQuote.NotDirectRental, error.Message);
        Assert.Empty(await h.Db.Reservations.ToListAsync());
    }

    [Fact]
    public async Task Turning_off_direct_rental_on_the_menu_requires_a_priced_offer()
    {
        await using var h = await ReservationQueueHarness.CreateAsync(queueEnabled: false);
        h.Db.TenantModules.Add(new TenantModule(h.TenantId, PlatformModules.Rentals));
        await h.Db.SaveChangesAsync();
        var rentals = new RentalAssetService(h.Db, h.TenantProvider);
        var menu = new ModuleMenuItemService(h.Db);

        var offMenu = await rentals.SetDirectRentalAsync(h.RentalAssetId, false, CancellationToken.None);
        Assert.False(offMenu.DirectRental);
        Assert.False(await h.Db.RentalAssets.AsNoTracking().Select(asset => asset.DirectRental).SingleAsync());
        await rentals.SetDirectRentalAsync(h.RentalAssetId, true, CancellationToken.None);

        await menu.CreateAsync(h.TenantId, Menu(h.RentalAssetId), CancellationToken.None);
        var blocked = await Assert.ThrowsAsync<PricedOfferRequiredException>(() =>
            rentals.SetDirectRentalAsync(h.RentalAssetId, false, CancellationToken.None));
        Assert.Equal(PricedOfferRequiredException.DirectRentalMessage, blocked.Message);
        Assert.True(await h.Db.RentalAssets.AsNoTracking().Select(asset => asset.DirectRental).SingleAsync());

        var free = Offer(h, 0m);
        h.Db.CatalogProducts.Add(free.Product);
        h.Db.CatalogProductResourceRequirements.Add(free.Requirement);
        await h.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<PricedOfferRequiredException>(() =>
            rentals.SetDirectRentalAsync(h.RentalAssetId, false, CancellationToken.None));

        free.Product.Price = 80m;
        await h.Db.SaveChangesAsync();
        var turnedOff = await rentals.SetDirectRentalAsync(h.RentalAssetId, false, CancellationToken.None);
        Assert.False(turnedOff.DirectRental);
    }

    [Fact]
    public async Task Publishing_a_menu_item_requires_a_priced_offer()
    {
        await using var h = await ReservationQueueHarness.CreateAsync(queueEnabled: false);
        h.Db.TenantModules.Add(new TenantModule(h.TenantId, PlatformModules.Rentals));
        (await h.Db.RentalPricings.SingleAsync()).PricePerHour = 0m;
        await h.Db.SaveChangesAsync();
        var menu = new ModuleMenuItemService(h.Db);

        await Assert.ThrowsAsync<PricedOfferRequiredException>(() =>
            menu.CreateAsync(h.TenantId, Menu(h.RentalAssetId), CancellationToken.None));
        await Assert.ThrowsAsync<PricedOfferRequiredException>(() =>
            menu.CreateAsync(h.TenantId, Menu(null), CancellationToken.None));
        Assert.Empty(await h.Db.TenantModuleMenuItems.ToListAsync());

        (await h.Db.RentalPricings.SingleAsync()).PricePerHour = 100m;
        await h.Db.SaveChangesAsync();
        var created = await menu.CreateAsync(h.TenantId, Menu(h.RentalAssetId), CancellationToken.None);
        Assert.True(created.IsActive);

        var status = await new SetupStatusService(h.Db, h.TenantProvider).GetAsync(CancellationToken.None);
        Assert.DoesNotContain("preco", status.Resources.Single().Missing);

        (await h.Db.RentalAssets.SingleAsync()).DirectRental = false;
        await h.Db.SaveChangesAsync();
        status = await new SetupStatusService(h.Db, h.TenantProvider).GetAsync(CancellationToken.None);
        Assert.Contains("preco", status.Resources.Single().Missing);
    }

    private static CheckAvailabilityRequestDto Request(ReservationQueueHarness h) => new()
    {
        AssetId = h.AssetId,
        Date = ReservationQueueHarness.BookDate,
        StartTime = ReservationQueueHarness.BookStart,
        EndTime = ReservationQueueHarness.BookEnd,
    };

    private static UpsertModuleMenuItemRequestDto Menu(Guid? rentalAssetId) => new()
    {
        ModuleName = PlatformModules.Rentals,
        Label = "Reservar horário",
        SortOrder = 0,
        IsActive = true,
        RentalAssetId = rentalAssetId,
    };

    private static (CatalogProduct Product, CatalogProductResourceRequirement Requirement) Offer(
        ReservationQueueHarness h, decimal? price)
    {
        var product = new CatalogProduct
        {
            TenantId = h.TenantId,
            Name = "Hora marcada",
            Price = price,
            IsActive = true,
        };
        var requirement = new CatalogProductResourceRequirement
        {
            TenantId = h.TenantId,
            ProductId = product.Id,
            RentalAssetId = h.RentalAssetId,
            ResourceQuantity = 1,
            DurationMinutesPerUnit = 60,
            IsActive = true,
            Version = 1,
        };
        return (product, requirement);
    }

    private static RentalPricing Price(decimal price) => new()
    {
        TenantId = Guid.NewGuid(),
        RentalAssetId = Guid.NewGuid(),
        DayOfWeek = DayOfWeek.Monday,
        StartTime = new TimeOnly(8, 0),
        EndTime = new TimeOnly(22, 0),
        PricePerHour = price,
        RequiresDeposit = false,
        DepositPercentage = 0,
    };
}
