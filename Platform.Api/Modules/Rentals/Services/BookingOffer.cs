using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>Shared quote totals and pricing selection for the two booking routes.</summary>
internal static class BookingOffer
{
    public static IOrderedQueryable<RentalPricing> ForPersistedSlot(IQueryable<RentalPricing> prices) =>
        prices.OrderByDescending(price => price.StartTime);

    public static RentalPricing? ForDerivedWindow(IEnumerable<RentalPricing> prices, decimal hours, int quantity) =>
        prices.OrderBy(price => price.PricePerHour)
            .OrderBy(price => Total(price, hours, quantity))
            .FirstOrDefault();

    public static decimal Total(RentalPricing pricing, decimal hours, int quantity, int? calendarDays = null) =>
        Total(pricing.PricePerHour, pricing.PricingUnit, hours, quantity, calendarDays);

    public static decimal Total(decimal hourlyPrice, decimal hours, int quantity) =>
        Math.Round(hourlyPrice * hours * quantity, 2, MidpointRounding.AwayFromZero);

    public static decimal Total(decimal unitPrice, PricingUnit unit, decimal hours, int quantity, int? calendarDays = null) =>
        unit switch
        {
            PricingUnit.Hour => Total(unitPrice, hours, quantity),
            PricingUnit.Slot => Math.Round(unitPrice * quantity, 2, MidpointRounding.AwayFromZero),
            PricingUnit.Day => Math.Round(unitPrice * (calendarDays ?? Math.Max(1m, Math.Ceiling(hours / 24m))) * quantity,
                2, MidpointRounding.AwayFromZero),
            PricingUnit.Fixed => Math.Round(unitPrice, 2, MidpointRounding.AwayFromZero),
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown pricing unit."),
        };

    public static decimal PricedUnits(PricingUnit unit, decimal hours, int quantity, int? calendarDays = null) =>
        unit switch
        {
            PricingUnit.Hour => hours * quantity,
            PricingUnit.Slot => quantity,
            PricingUnit.Day => (calendarDays ?? Math.Max(1m, Math.Ceiling(hours / 24m))) * quantity,
            PricingUnit.Fixed => 1m,
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown pricing unit."),
        };
}
