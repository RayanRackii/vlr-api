namespace Platform.Api.Modules.Rentals;

/// <summary>
/// Customer self-booking exceeded the tenant's per-customer civil-day cap.
/// </summary>
public sealed class DailyBookingLimitReachedException : InvalidOperationException
{
    public const string Code = "DAILY_BOOKING_LIMIT_REACHED";

    public DailyBookingLimitReachedException(int limit)
        : base($"Limite de {limit} reservas ativas por dia atingido.")
    {
        Limit = limit;
    }

    public int Limit { get; }
}
