namespace Platform.Api.Modules.Rentals;

/// <summary>
/// A customer booking starts sooner than the effective minimum lead time
/// (resource override, otherwise category policy, otherwise the tenant policy).
/// </summary>
public sealed class BookingLeadTimeNotMetException : ArgumentException
{
    public const string Code = "booking_lead_time_not_met";

    public int MinimumBookingLeadMinutes { get; }

    public BookingLeadTimeNotMetException(int minimumBookingLeadMinutes)
        : base($"A reserva precisa ser feita com pelo menos {minimumBookingLeadMinutes} minutos de antecedência.")
    {
        MinimumBookingLeadMinutes = minimumBookingLeadMinutes;
    }
}
