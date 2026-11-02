namespace Platform.Api.Modules.Catalog.Services;

/// <summary>Phase 0 work windows fit within one day, with at least one minute per unit.</summary>
internal static class CatalogPlanningWindow
{
    internal const int MaxMinutes = 24 * 60;

    public static (DateTimeOffset WorkEnd, DateTimeOffset OccupiedStart, DateTimeOffset OccupiedEnd) Calculate(
        DateTimeOffset workStart, int durationMinutesPerUnit, int quantity, int prepMinutes, int cleanupMinutes)
    {
        if (durationMinutesPerUnit is < 1 or > MaxMinutes || quantity is < 1 or > MaxMinutes
            || prepMinutes is < 0 or > MaxMinutes || cleanupMinutes is < 0 or > MaxMinutes)
        {
            throw new ArgumentException("Planning duration, quantity and buffers must fit within a single day.");
        }

        try
        {
            var minutes = checked(durationMinutesPerUnit * quantity);
            if (minutes > MaxMinutes)
            {
                throw new ArgumentException("The total planned work duration must fit within a single day.");
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
