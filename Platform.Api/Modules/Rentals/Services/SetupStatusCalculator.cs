namespace Platform.Api.Modules.Rentals.Services;

/// <summary>
/// Derives rascunho / falta / publicado from current records. Nothing here is a saved progress flag.
/// </summary>
public static class SetupStatusCalculator
{
    public const string Draft = "draft";
    public const string Missing = "missing";
    public const string Published = "published";

    public readonly record struct ResourceInput(
        bool HasName,
        bool HasHours,
        bool HasPositivePrice,
        bool QueueBlocked,
        bool IsActive,
        bool OnMenu);

    public readonly record struct ChecklistInput(
        bool CompanyProfileReady,
        bool PricedOfferReachesResource,
        bool HoursDefined,
        int ResourceCount,
        int PublishedResourceCount,
        bool PendingInvite,
        bool InviteAccepted);

    public static (string State, string[] Missing) Resource(ResourceInput input)
    {
        var missing = new List<string>(4);
        if (!input.HasName) missing.Add("dados");
        if (!input.HasHours) missing.Add("horarios");
        if (!input.HasPositivePrice) missing.Add("preco");
        if (input.QueueBlocked) missing.Add("fila");
        if (missing.Count > 0) return (Missing, missing.ToArray());
        return (input.IsActive && input.OnMenu ? Published : Draft, []);
    }

    public static (string Key, string State)[] Checklist(ChecklistInput input)
    {
        var resourceState = input.PublishedResourceCount > 0
            ? Published
            : input.ResourceCount > 0
                ? Draft
                : Missing;
        var adminState = input.InviteAccepted
            ? Published
            : input.PendingInvite
                ? Missing
                : Draft;

        return
        [
            ("company", input.CompanyProfileReady ? Published : Missing),
            ("pricedOffer", input.PricedOfferReachesResource ? Published : Missing),
            ("hours", input.HoursDefined ? Published : Missing),
            ("resource", resourceState),
            ("admin", adminState),
            // A customer page exists only when a resource is actually published. Opening the preview stores nothing.
            ("preview", input.PublishedResourceCount > 0 ? Published : Missing),
        ];
    }
}
