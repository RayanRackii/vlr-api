using Platform.Core.Domain.Entities;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>Pricing selection preserves the two existing booking routes' precedence.</summary>
internal static class BookingOffer
{
    public static IOrderedQueryable<RentalPricing> ForPersistedSlot(IQueryable<RentalPricing> prices) =>
        prices.OrderByDescending(price => price.StartTime);

    public static IOrderedQueryable<RentalPricing> ForDerivedWindow(IQueryable<RentalPricing> prices) =>
        prices.OrderBy(price => price.PricePerHour);

    public static decimal Total(decimal hourlyPrice, decimal hours, int quantity) =>
        Math.Round(hourlyPrice * hours * quantity, 2, MidpointRounding.AwayFromZero);
}
