using Platform.Api.Services.Tenancy;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.ResourcePlanning.Services;

/// <summary>One weekly open window: wall-clock times of the tenant zone on a day of the week.</summary>
public readonly record struct WorkingHoursWindow(DayOfWeek Day, TimeOnly Open, TimeOnly Close);

/// <summary>
/// Pure working-hours rules of resource planning. Given the hours policy of an asset, its weekly
/// windows, the tenant zone and an interval, returns <c>null</c> when the interval is allowed or a
/// reason code when it is not. Hours reject (they never merely warn).
/// </summary>
/// <remarks>
/// Windows are wall-clock in the tenant zone and are turned into instants per calendar day with
/// <see cref="TenantTimeZoneProvider.AtLocal"/>: a nonexistent time (spring-forward gap) shifts
/// forward by the gap and an ambiguous time (fall-back) uses the earlier instant, so a window never
/// has a negative length and a DST day is judged on real instants. A close time of 23:59 or later
/// means "until midnight", so back-to-back days chain into one continuous window and a multi-day
/// job fits an asset that is open around the clock on several days. Several windows on a day
/// (lunch break) are allowed; touching or overlapping windows merge.
/// </remarks>
public static class AssetWorkingHoursRules
{
    /// <summary>The start of the interval is not inside a working window (StartWithinHours).</summary>
    public const string StartOutsideHours = "start_outside_working_hours";

    /// <summary>Part of the interval is not covered by working windows (WithinHours).</summary>
    public const string OutsideHours = "outside_working_hours";

    /// <summary>The interval is empty, inverted or longer than <see cref="MaxIntervalDays"/>.</summary>
    public const string InvalidInterval = "invalid_interval";

    /// <summary>Longest interval the rules evaluate; callers cap durations far below this.</summary>
    public const int MaxIntervalDays = 366;

    /// <summary>A close time at or after this value means "until midnight".</summary>
    public static readonly TimeOnly EndOfDay = new(23, 59);

    public static string? Validate(
        AssetHoursPolicy policy,
        IReadOnlyCollection<WorkingHoursWindow> weekly,
        TimeZoneInfo zone,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        ArgumentNullException.ThrowIfNull(weekly);
        ArgumentNullException.ThrowIfNull(zone);

        if (end <= start || end - start > TimeSpan.FromDays(MaxIntervalDays))
        {
            return InvalidInterval;
        }

        switch (policy)
        {
            case AssetHoursPolicy.Anytime:
                return null;

            case AssetHoursPolicy.StartWithinHours:
            {
                var startDate = LocalDate(start, zone);
                foreach (var window in InstantWindows(weekly, zone, startDate, startDate))
                {
                    if (window.Start <= start && start < window.End)
                    {
                        return null;
                    }
                }

                return StartOutsideHours;
            }

            case AssetHoursPolicy.WithinHours:
            {
                var merged = Merge(InstantWindows(
                    weekly,
                    zone,
                    LocalDate(start, zone),
                    LocalDate(end.AddTicks(-1), zone)));
                foreach (var window in merged)
                {
                    if (window.Start <= start && end <= window.End)
                    {
                        return null;
                    }
                }

                return OutsideHours;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown hours policy.");
        }
    }

    private static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    private static List<(DateTimeOffset Start, DateTimeOffset End)> InstantWindows(
        IReadOnlyCollection<WorkingHoursWindow> weekly,
        TimeZoneInfo zone,
        DateOnly firstDate,
        DateOnly lastDate)
    {
        var result = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        for (var date = firstDate; date <= lastDate; date = date.AddDays(1))
        {
            foreach (var window in weekly)
            {
                if (window.Day != date.DayOfWeek)
                {
                    continue;
                }

                var open = TenantTimeZoneProvider.AtLocal(date, window.Open, zone);
                var close = window.Close >= EndOfDay
                    ? TenantTimeZoneProvider.AtLocal(date.AddDays(1), TimeOnly.MinValue, zone)
                    : TenantTimeZoneProvider.AtLocal(date, window.Close, zone);
                if (close > open)
                {
                    result.Add((open, close));
                }
            }
        }

        return result;
    }

    private static List<(DateTimeOffset Start, DateTimeOffset End)> Merge(
        List<(DateTimeOffset Start, DateTimeOffset End)> windows)
    {
        windows.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var window in windows)
        {
            if (merged.Count > 0 && window.Start <= merged[^1].End)
            {
                var last = merged[^1];
                merged[^1] = (last.Start, window.End > last.End ? window.End : last.End);
            }
            else
            {
                merged.Add(window);
            }
        }

        return merged;
    }
}
