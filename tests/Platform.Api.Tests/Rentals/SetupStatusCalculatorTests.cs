using Platform.Api.Modules.Rentals.Services;

namespace Platform.Api.Tests.Rentals;

public sealed class SetupStatusCalculatorTests
{
    [Fact]
    public void Resource_without_hours_or_price_is_missing_those_items()
    {
        var (state, missing) = SetupStatusCalculator.Resource(new SetupStatusCalculator.ResourceInput(
            HasName: true,
            HasHours: false,
            HasPositivePrice: false,
            QueueBlocked: false,
            IsActive: true,
            OnMenu: true));

        Assert.Equal(SetupStatusCalculator.Missing, state);
        Assert.Equal(new[] { "horarios", "preco" }, missing);
    }

    [Fact]
    public void Resource_ready_but_not_on_the_menu_stays_a_draft()
    {
        var (state, missing) = SetupStatusCalculator.Resource(new SetupStatusCalculator.ResourceInput(
            HasName: true,
            HasHours: true,
            HasPositivePrice: true,
            QueueBlocked: false,
            IsActive: true,
            OnMenu: false));

        Assert.Equal(SetupStatusCalculator.Draft, state);
        Assert.Empty(missing);
    }

    [Fact]
    public void Checklist_follows_records_and_does_not_store_a_preview_flag()
    {
        var items = SetupStatusCalculator.Checklist(new SetupStatusCalculator.ChecklistInput(
            CompanyProfileReady: true,
            PricedOfferReachesResource: true,
            HoursDefined: false,
            ResourceCount: 2,
            PublishedResourceCount: 0,
            PendingInvite: true,
            InviteAccepted: false));

        Assert.Equal(
            new[] { "published", "published", "missing", "draft", "missing", "missing" },
            items.Select(item => item.State).ToArray());
    }

    [Fact]
    public void Published_resource_makes_the_preview_row_published()
    {
        var items = SetupStatusCalculator.Checklist(new SetupStatusCalculator.ChecklistInput(
            CompanyProfileReady: true,
            PricedOfferReachesResource: true,
            HoursDefined: true,
            ResourceCount: 1,
            PublishedResourceCount: 1,
            PendingInvite: false,
            InviteAccepted: true));

        Assert.Equal(SetupStatusCalculator.Published, items.Single(item => item.Key == "preview").State);
    }
}
