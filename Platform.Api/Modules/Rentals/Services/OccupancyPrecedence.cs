using Platform.Core.Domain.Entities;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>
/// Rank and interval helpers for overlapping <c>ScheduleTemplate</c> rows.
/// Known keys: closed=3, lesson=2, open=1. Other kinds: 2 if they block capacity, else 1.
/// Equal rank is broken by ordinal <c>Key</c> (higher wins).
/// </summary>
public static class OccupancyPrecedence
{
    public readonly record struct WinningWindow(
        ScheduleTemplate Template,
        TimeOnly Start,
        TimeOnly End);
    public static int Rank(string key, bool blocksCapacity)
    {
        var normalized = key.Trim().ToLowerInvariant();
        return normalized switch
        {
            "closed" => 3,
            "lesson" => 2,
            "open" => 1,
            _ => blocksCapacity ? 2 : 1,
        };
    }

    public static bool IntervalsOverlap(TimeOnly startA, TimeOnly endA, TimeOnly startB, TimeOnly endB) =>
        startA < endB && endA > startB;

    public static int Compare(string keyA, bool blocksCapacityA, string keyB, bool blocksCapacityB)
    {
        var rank = Rank(keyA, blocksCapacityA).CompareTo(Rank(keyB, blocksCapacityB));
        return rank != 0 ? rank : string.CompareOrdinal(keyA, keyB);
    }

    /// <summary>
    /// Splits overlapping weekday templates at breakpoints. The highest-rank winner
    /// occupies each segment; adjacent segments from the same template are merged.
    /// </summary>
    public static List<WinningWindow> SplitWinningWindows(
        IReadOnlyList<ScheduleTemplate> rentableTemplates)
    {
        var breakpoints = rentableTemplates
            .SelectMany(t => new[] { t.StartTime, t.EndTime })
            .Distinct()
            .OrderBy(t => t)
            .ToList();

        var segments = new List<WinningWindow>();
        ScheduleTemplate? mergeWinner = null;
        TimeOnly mergeStart = default;
        TimeOnly mergeEnd = default;

        for (var i = 0; i < breakpoints.Count - 1; i++)
        {
            var segStart = breakpoints[i];
            var segEnd = breakpoints[i + 1];
            if (segStart >= segEnd)
            {
                continue;
            }

            var covering = rentableTemplates
                .Where(t => t.StartTime <= segStart && t.EndTime >= segEnd)
                .ToList();
            if (covering.Count == 0)
            {
                Flush();
                mergeWinner = null;
                continue;
            }

            var winner = covering
                .OrderByDescending(t => Rank(t.OccupancyKind.Key, t.OccupancyKind.BlocksCapacity))
                .ThenByDescending(t => t.OccupancyKind.Key, StringComparer.Ordinal)
                .ThenBy(t => t.Id)
                .First();

            if (mergeWinner is not null && mergeWinner.Id == winner.Id && mergeEnd == segStart)
            {
                mergeEnd = segEnd;
                continue;
            }

            Flush();
            mergeWinner = winner;
            mergeStart = segStart;
            mergeEnd = segEnd;
        }

        Flush();
        return segments;

        void Flush()
        {
            if (mergeWinner is not null)
            {
                segments.Add(new WinningWindow(mergeWinner, mergeStart, mergeEnd));
            }
        }
    }
}
