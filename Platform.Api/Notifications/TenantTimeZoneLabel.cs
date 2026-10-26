using System.Globalization;

namespace Platform.Api.Notifications;

public static class TenantTimeZoneLabel
{
    public static string Format(string ianaId, DateTimeOffset reservationStart)
    {
        var city = ianaId switch
        {
            "America/Sao_Paulo" => "São Paulo",
            "America/Manaus" => "Manaus",
            "America/New_York" => "Nova York",
            "America/Belem" => "Belém",
            "America/Cuiaba" => "Cuiabá",
            "America/Rio_Branco" => "Rio Branco",
            "America/Noronha" => "Fernando de Noronha",
            "Europe/Lisbon" => "Lisboa",
            "Europe/London" => "Londres",
            "Asia/Tokyo" => "Tóquio",
            _ => ianaId[(ianaId.LastIndexOf('/') + 1)..].Replace('_', ' '),
        };
        TimeSpan offset;
        try
        {
            offset = TimeZoneInfo.FindSystemTimeZoneById(ianaId).GetUtcOffset(reservationStart);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return $"horário de {city}";
        }
        var gmt = "GMT";
        if (offset != TimeSpan.Zero)
        {
            var magnitude = offset.Duration();
            gmt += offset < TimeSpan.Zero ? "-" : "+";
            gmt += magnitude.Hours.ToString(CultureInfo.InvariantCulture);
            if (magnitude.Minutes != 0)
            {
                gmt += ":" + magnitude.Minutes.ToString("00", CultureInfo.InvariantCulture);
            }
        }

        return $"horário de {city}, {gmt}";
    }
}
