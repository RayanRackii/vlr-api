namespace Platform.Api.Modules.Rentals.Services;

/// <summary>
/// Booking window on the tenant civil clock (<see cref="TenantBookingClock"/>:
/// tenant setting, otherwise <c>America/Sao_Paulo</c>). The injected <see cref="TimeProvider"/> is the only "now".
/// A start equal to that instant is still allowed; only an earlier start is rejected.
/// </summary>
internal static class ReservationBookingRules
{
    public const string PastStartMessage =
        "O início do horário solicitado já passou.";

    public static string LocationUnavailable(string assetName) =>
        $"O local '{assetName}' não está disponível para o intervalo solicitado.";

    public static bool StartsBeforeNow(DateTimeOffset start, TimeProvider clock) =>
        start < clock.GetUtcNow();

    public static void EnsureStartNotBeforeNow(
        DateTimeOffset start,
        TimeProvider clock,
        string? message = null)
    {
        if (StartsBeforeNow(start, clock))
        {
            throw new StartInThePastException(message ?? PastStartMessage);
        }
    }
}
