namespace Platform.Api.Modules.Rentals.Services;

/// <summary>
/// Rank and interval helpers for overlapping <c>ScheduleTemplate</c> rows.
/// Known keys: closed=3, lesson=2, open=1. Other kinds: 2 if they block capacity, else 1.
/// Equal rank is broken by ordinal <c>Key</c> (higher wins).
/// </summary>
public static class OccupancyPrecedence
{
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
}
