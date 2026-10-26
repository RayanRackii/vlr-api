using Platform.Core.Domain.Entities;

namespace Platform.Api.Modules.Rentals.Services;

internal static class OpenHoursRules
{
    internal static int DailyWindowMinutes(TimeOnly open, TimeOnly close)
    {
        var minutes = (int)(close - open).TotalMinutes;
        return minutes <= 0 ? minutes + 1440 : minutes;
    }

    internal static IReadOnlyList<int> ParseDurations(string? value)
    {
        var durations = (value ?? "").Split(',')
            .Select(s => int.TryParse(s.Trim(), out var minutes) ? minutes : 0)
            .Where(minutes => minutes is > 0 and <= 1440).Distinct().Order().ToList();
        return durations.Count == 0 ? [60] : durations;
    }

    internal static string? ValidateInterval(RentalAsset rental, TimeOnly start, TimeOnly end, string name,
        bool enforceAllowedDurations = true)
    {
        if (rental.OpenTime is not { } open || rental.CloseTime is not { } close
            || close <= open || start < open || TenantBookingClock.EndOfDayTime(end) > TenantBookingClock.EndOfDayTime(close) || end <= start)
        {
            return $"'{name}' is not open for the requested interval.";
        }

        // Multi-day segments (start->close, open->close, open->end) only need to fit opening hours;
        // allowed durations describe single-day bookings.
        if (!enforceAllowedDurations)
        {
            return null;
        }

        // Requests and opening hours are tenant-local civil times, including across DST.
        var minutes = (TenantBookingClock.EndOfDayTime(end) - start.ToTimeSpan()).TotalMinutes;
        return ParseDurations(rental.AllowedDurationMinutes).Any(allowed => allowed == minutes)
            ? null
            : $"'{name}' requested duration is not allowed.";
    }
}
