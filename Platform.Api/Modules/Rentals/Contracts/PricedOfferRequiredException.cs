namespace Platform.Api.Modules.Rentals.Contracts;

public sealed class PricedOfferRequiredException(string message) : InvalidOperationException(message)
{
    public const string Code = "PRICED_OFFER_REQUIRED";

    public const string PublishMessage =
        "A priced offer must reach this resource before it can be published.";

    public const string DirectRentalMessage =
        "Direct rental cannot be turned off while this resource is on the menu and no priced offer reaches it.";
}
