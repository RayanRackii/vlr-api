using Platform.Core.Domain.Constants;

namespace Platform.Api.Notifications;

public static class WhatsAppParameterBinder
{
    public static IReadOnlyList<string> Bind(
        string eventType,
        IReadOnlyDictionary<string, string?> payload)
    {
        var keys = KeysFor(eventType);
        return keys.Select(key => payload.GetValueOrDefault(key) ?? string.Empty).ToArray();
    }

    private static string[] KeysFor(string eventType)
    {
        if (eventType == RentalEventTypes.ReservationReminder)
        {
            return ["tenantName", "customerName", "reservationReference", "reservationDateTime"];
        }

        if (eventType.StartsWith("rentals.", StringComparison.Ordinal))
        {
            return ["tenantName", "customerName", "reservationReference", "reservationStatus"];
        }

        if (eventType.StartsWith("catalog.", StringComparison.Ordinal))
        {
            return ["tenantName", "customerName", "orderNumber", "orderStatus"];
        }

        throw new InvalidOperationException($"No WhatsApp parameter map for '{eventType}'.");
    }
}
