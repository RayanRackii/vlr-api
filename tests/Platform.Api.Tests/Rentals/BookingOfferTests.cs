using Platform.Api.Modules.Rentals.Services;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Rentals;

public sealed class BookingOfferTests
{
    [Theory]
    [InlineData(PricingUnit.Hour, 20.01, 0.5, 3, 30.02)]
    [InlineData(PricingUnit.Slot, 20.01, 0.5, 3, 60.03)]
    [InlineData(PricingUnit.Day, 20.01, 0.5, 3, 60.03)]
    [InlineData(PricingUnit.Day, 20, 24, 2, 40)]
    [InlineData(PricingUnit.Day, 20, 24.01, 2, 80)]
    [InlineData(PricingUnit.Day, 20, 0, 2, 40)]
    [InlineData(PricingUnit.Fixed, 20.01, 48, 3, 20.01)]
    [InlineData(PricingUnit.Fixed, 20.005, 0.5, 3, 20.01)]
    [InlineData(PricingUnit.Slot, 20.005, 2, 1, 20.01)]
    public void Total_obeys_the_pricing_unit(
        PricingUnit unit, decimal price, decimal hours, int quantity, decimal expected) =>
        Assert.Equal(expected, BookingOffer.Total(price, unit, hours, quantity));

    [Theory]
    [InlineData(0.5, 1, PricingUnit.Hour)]
    [InlineData(2, 1, PricingUnit.Day)]
    [InlineData(25, 1, PricingUnit.Slot)]
    [InlineData(2, 3, PricingUnit.Fixed)]
    public void Derived_window_selects_the_lowest_total_for_duration_and_quantity(
        decimal hours, int quantity, PricingUnit expected)
    {
        var prices = new[] { Price(30, PricingUnit.Fixed), Price(15, PricingUnit.Slot),
            Price(12, PricingUnit.Day), Price(10, PricingUnit.Hour) };

        Assert.Equal(expected, BookingOffer.ForDerivedWindow(prices, hours, quantity)!.PricingUnit);
    }

    [Fact]
    public void Equal_totals_preserve_the_existing_price_order()
    {
        var hourly = Price(10, PricingUnit.Hour);
        Assert.Same(hourly, BookingOffer.ForDerivedWindow([Price(20, PricingUnit.Slot), hourly], 2, 1));
    }

    [Theory]
    [InlineData(0.5, 3, 15.01)]
    [InlineData(2, 3, 60.03)]
    [InlineData(0, 3, 0)]
    public void All_hour_prices_keep_the_existing_selection_and_rounding(decimal hours, int quantity, decimal expected)
    {
        var cheapest = Price(10.005m, PricingUnit.Hour);
        var selected = BookingOffer.ForDerivedWindow([Price(15, PricingUnit.Hour), cheapest], hours, quantity);

        Assert.Same(cheapest, selected);
        Assert.Equal(expected, BookingOffer.Total(selected!, hours, quantity));
        Assert.Equal(expected, BookingOffer.Total(cheapest.PricePerHour, hours, quantity));
    }

    private static RentalPricing Price(decimal price, PricingUnit unit) => new()
    {
        TenantId = Guid.NewGuid(), RentalAssetId = Guid.NewGuid(), DayOfWeek = DayOfWeek.Monday,
        StartTime = new(8, 0), EndTime = new(22, 0), PricePerHour = price, PricingUnit = unit,
        RequiresDeposit = false, DepositPercentage = 0,
    };
}
