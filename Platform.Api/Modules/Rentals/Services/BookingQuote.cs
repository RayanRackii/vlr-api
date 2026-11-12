using Platform.Core.Domain.Entities;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>
/// Customer quotes use a positive price only. A missing or zero price is not R$ 0.
/// </summary>
internal static class BookingQuote
{
    public const string NotDirectRental = "This resource is not offered as a direct rental.";

    public const string PriceMustBePositive = "PricePerHour must be greater than zero.";

    public static void RequireDirectRental(RentalAsset rental)
    {
        if (!rental.DirectRental)
        {
            throw new InvalidOperationException(NotDirectRental);
        }
    }

    public static void RequirePositive(decimal price)
    {
        if (price <= 0)
        {
            throw new InvalidOperationException(PriceMustBePositive);
        }
    }
}
