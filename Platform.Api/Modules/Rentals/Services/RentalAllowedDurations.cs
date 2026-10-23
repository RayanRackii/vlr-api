namespace Platform.Api.Modules.Rentals.Services;

public static class RentalAllowedDurations
{
    public static string Normalize(string value)
    {
        var durations = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, out var minutes) ? minutes : 0)
            .ToArray();

        if (durations.Length == 0 || durations.Any(minutes => minutes is <= 0 or >= 1440))
        {
            throw new ArgumentException("Allowed durations must be comma-separated whole minutes between 1 and 1439.");
        }

        return string.Join(',', durations.Distinct().Order());
    }
}
