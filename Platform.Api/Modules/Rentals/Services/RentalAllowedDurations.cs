namespace Platform.Api.Modules.Rentals.Services;

public static class RentalAllowedDurations
{
    public static string Normalize(
        string value,
        int slotMinutes = 60,
        int? openingWindowMinutes = null)
    {
        if (slotMinutes <= 0)
        {
            throw new ArgumentException("SlotMinutes deve ser maior que zero.");
        }

        var durations = value.Split(',', StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var minutes) ? minutes : (int?)null)
            .ToArray();

        if (durations.Length == 0 || durations.Any(minutes => minutes is null or <= 0))
        {
            throw new ArgumentException("AllowedDurationMinutes deve conter minutos inteiros positivos separados por vírgula.");
        }

        var values = durations.Select(minutes => minutes!.Value).ToArray();
        var errors = new List<string>();
        var duplicates = values.GroupBy(minutes => minutes).Where(group => group.Count() > 1)
            .Select(group => group.Key).Order();
        if (duplicates.Any())
        {
            errors.Add($"valores duplicados não são permitidos: {string.Join(", ", duplicates)}");
        }

        foreach (var minutes in values.Distinct().Order())
        {
            if (minutes > 1440)
            {
                errors.Add($"{minutes} excede o limite máximo de 1440 min");
            }
            else if (minutes % slotMinutes != 0)
            {
                errors.Add($"{minutes} não é múltiplo do slot de {slotMinutes} min");
            }

            if (openingWindowMinutes is { } window && minutes > window)
            {
                errors.Add($"{minutes} excede a janela de funcionamento de {window} min");
            }
        }

        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join("; ", errors));
        }

        return string.Join(',', values.Order());
    }
}
