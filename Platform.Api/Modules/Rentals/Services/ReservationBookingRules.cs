namespace Platform.Api.Modules.Rentals.Services;

/// <summary>
/// Booking window in the platform civil clock (<c>America/Sao_Paulo</c> via
/// <see cref="Platform.Core.Infrastructure.Time.BrazilTimeZone"/>). The injected
/// <see cref="TimeProvider"/> is the only "now".
/// </summary>
internal static class ReservationBookingRules
{
    public const string PastStartMessage =
        "O início do horário solicitado já passou.";

    public static string LocationUnavailable(string assetName) =>
        $"O local '{assetName}' não está disponível para o intervalo solicitado.";

    public static bool StartsBeforeNow(DateTimeOffset start, TimeProvider clock) =>
        start < clock.GetUtcNow();

    public static void EnsureStartNotBeforeNow(DateTimeOffset start, TimeProvider clock)
    {
        if (StartsBeforeNow(start, clock))
        {
            throw new ArgumentException(PastStartMessage);
        }
    }
}
