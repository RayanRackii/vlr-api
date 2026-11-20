namespace Platform.Api.Modules.Catalog.Services;

/// <summary>
/// Work windows fit within one day on a rental-backed asset (Phase 0) and within the planning-only
/// limit (30 days) on any other asset, with at least one minute per unit. The caller passes the limit
/// of the asset the order lands on.
/// </summary>
internal static class CatalogPlanningWindow
{
    internal const int MaxMinutes = 24 * 60;

    public static (DateTimeOffset WorkEnd, DateTimeOffset OccupiedStart, DateTimeOffset OccupiedEnd) Calculate(
        DateTimeOffset workStart,
        int durationMinutesPerUnit,
        int quantity,
        int prepMinutes,
        int cleanupMinutes,
        int maxWorkMinutes = MaxMinutes)
    {
        if (durationMinutesPerUnit is < 1 || durationMinutesPerUnit > maxWorkMinutes
            || quantity is < 1 || quantity > maxWorkMinutes
            || prepMinutes is < 0 or > MaxMinutes || cleanupMinutes is < 0 or > MaxMinutes)
        {
            throw new ArgumentException(maxWorkMinutes == MaxMinutes
                ? "Planning duration, quantity and buffers must fit within a single day."
                : "Planning duration, quantity and buffers are outside the supported range.");
        }

        try
        {
            var minutes = checked(durationMinutesPerUnit * quantity);
            if (minutes > maxWorkMinutes)
            {
                throw new ArgumentException(maxWorkMinutes == MaxMinutes
                    ? "The total planned work duration must fit within a single day."
                    : $"The total planned work duration must not exceed {maxWorkMinutes / MaxMinutes} days.");
            }
            var workEnd = workStart.AddTicks(checked((long)minutes * TimeSpan.TicksPerMinute));
            return (workEnd, workStart.AddMinutes(-prepMinutes), workEnd.AddMinutes(cleanupMinutes));
        }
        catch (OverflowException)
        {
            throw new ArgumentException("The planned duration is outside the supported range.");
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ArgumentException("The planned interval is outside the supported date range.");
        }
    }
}
