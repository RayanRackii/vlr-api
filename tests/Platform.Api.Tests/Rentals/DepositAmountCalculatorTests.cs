using Platform.Api.Modules.Rentals.Services;
using Platform.Core.Domain.Entities;

namespace Platform.Api.Tests.Rentals;

public sealed class DepositAmountCalculatorTests
{
    private static BookingPolicy Policy(int? percent) => new() { TenantId = Guid.NewGuid(), DepositPercent = percent };

    [Fact]
    public void Resource_percent_wins_over_every_other_level()
    {
        var percent = DepositAmountCalculator.ResolvePercent(10, Policy(20), Policy(30), 40m);
        Assert.Equal(10, percent);
    }

    [Fact]
    public void Category_percent_wins_when_the_resource_has_none()
    {
        var percent = DepositAmountCalculator.ResolvePercent(null, Policy(20), Policy(30), 40m);
        Assert.Equal(20, percent);
    }

    [Fact]
    public void Tenant_percent_wins_when_resource_and_category_have_none()
    {
        var percent = DepositAmountCalculator.ResolvePercent(null, Policy(null), Policy(30), 40m);
        Assert.Equal(30, percent);
    }

    [Fact]
    public void Pricing_percentage_applies_when_no_policy_sets_one()
    {
        var percent = DepositAmountCalculator.ResolvePercent(null, null, null, 40m);
        Assert.Equal(40, percent);
    }

    [Theory]
    [InlineData(33.5, 34)]
    [InlineData(33.4, 33)]
    public void Pricing_percentage_rounds_away_from_zero(double pricing, int expected)
    {
        Assert.Equal(expected, DepositAmountCalculator.ResolvePercent(null, null, null, (decimal)pricing));
    }

    [Fact]
    public void Falls_back_to_100_when_nothing_is_configured()
    {
        Assert.Equal(100, DepositAmountCalculator.ResolvePercent(null, null, null, null));
    }

    [Fact]
    public void A_zero_resource_percent_is_a_value_and_not_a_fallthrough()
    {
        Assert.Equal(0, DepositAmountCalculator.ResolvePercent(0, Policy(20), Policy(30), 40m));
    }

    [Theory]
    [InlineData("10.05", 50, "5.03")]   // 5.025 -> 5.03
    [InlineData("10.01", 50, "5.01")]   // 5.005 -> 5.01
    [InlineData("10.04", 50, "5.02")]   // exact
    [InlineData("100.00", 30, "30.00")]
    public void Amount_rounds_the_midpoint_away_from_zero(string total, int percent, string expected)
    {
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            DepositAmountCalculator.AmountFor(decimal.Parse(total, System.Globalization.CultureInfo.InvariantCulture), percent));
    }

    [Fact]
    public void Several_items_use_the_highest_percent()
    {
        var items = new[]
        {
            new DepositItemInput(10, null),
            new DepositItemInput(null, Policy(60)),
            new DepositItemInput(null, null),
        };

        var amount = DepositAmountCalculator.Calculate(items, Policy(30), null, 200m);

        Assert.Equal(120m, amount);
    }

    [Fact]
    public void Items_with_unset_levels_each_resolve_independently()
    {
        var items = new[] { new DepositItemInput(25, null), new DepositItemInput(null, null) };

        Assert.Equal(40, DepositAmountCalculator.ResolvePercent(items, null, 40m));
    }

    [Fact]
    public void No_items_resolve_through_the_tenant_then_pricing_then_100()
    {
        Assert.Equal(30, DepositAmountCalculator.ResolvePercent([], Policy(30), 40m));
        Assert.Equal(100, DepositAmountCalculator.ResolvePercent([], null, null));
    }

    [Fact]
    public void Zero_percent_means_no_charge()
    {
        var amount = DepositAmountCalculator.Calculate([new DepositItemInput(0, null)], null, null, 500m);
        Assert.Equal(0m, amount);
    }

    [Fact]
    public void Zero_total_means_no_charge()
    {
        var amount = DepositAmountCalculator.Calculate([new DepositItemInput(50, null)], null, null, 0m);
        Assert.Equal(0m, amount);
    }
}
