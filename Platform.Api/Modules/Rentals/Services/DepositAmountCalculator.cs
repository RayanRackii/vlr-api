using Platform.Core.Domain.Entities;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>One reservation item: its resource percent and its category policy (both optional).</summary>
public readonly record struct DepositItemInput(int? AssetDepositPercent, BookingPolicy? CategoryPolicy);

/// <summary>
/// Online deposit amount (ADR 0009, decision 6). Pure: no I/O, nothing stored on the reservation.
/// Percent per item: resource, then category, then tenant, then the pricing window percent (rounded), then 100.
/// With several items the highest percent wins. A result of 0 means no online charge.
/// </summary>
public static class DepositAmountCalculator
{
    public const int DefaultPercent = 100;

    public static int ResolvePercent(
        int? assetPercent, BookingPolicy? categoryPolicy, BookingPolicy? tenantPolicy, decimal? pricingPercentage) =>
        PolicyResolver.EffectiveDepositPercent(assetPercent, categoryPolicy, tenantPolicy)
        ?? (pricingPercentage is { } pricing
            ? (int)Math.Round(pricing, MidpointRounding.AwayFromZero)
            : DefaultPercent);

    public static int ResolvePercent(
        IEnumerable<DepositItemInput> items, BookingPolicy? tenantPolicy, decimal? pricingPercentage)
    {
        var percents = items
            .Select(i => ResolvePercent(i.AssetDepositPercent, i.CategoryPolicy, tenantPolicy, pricingPercentage))
            .ToList();
        return percents.Count == 0
            ? ResolvePercent(null, null, tenantPolicy, pricingPercentage)
            : percents.Max();
    }

    public static decimal Calculate(
        IEnumerable<DepositItemInput> items, BookingPolicy? tenantPolicy, decimal? pricingPercentage, decimal totalAmount) =>
        AmountFor(totalAmount, ResolvePercent(items, tenantPolicy, pricingPercentage));

    public static decimal AmountFor(decimal totalAmount, int percent) =>
        Math.Round(totalAmount * percent / 100m, 2, MidpointRounding.AwayFromZero);
}
