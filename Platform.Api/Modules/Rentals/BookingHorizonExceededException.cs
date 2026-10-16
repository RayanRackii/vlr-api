namespace Platform.Api.Modules.Rentals;

/// <summary>
/// The booking start falls after the effective advance horizon
/// (resource override, otherwise the tenant booking policy).
/// </summary>
public sealed class BookingHorizonExceededException : ArgumentException
{
    public const string Code = "booking_horizon_exceeded";

    public const string DefaultMessage =
        "A data solicitada está além do horizonte de reserva.";

    public BookingHorizonExceededException()
        : base(DefaultMessage)
    {
    }
}
