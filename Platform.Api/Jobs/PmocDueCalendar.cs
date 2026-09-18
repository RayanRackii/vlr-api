using Platform.Core.Domain.Enums;

namespace Platform.Api.Jobs;

public static class PmocDueCalendar
{
    public static bool IsDueToday(MaintenanceFrequency frequency, DateOnly today) =>
        frequency switch
        {
            MaintenanceFrequency.Daily => true,
            MaintenanceFrequency.Weekly => today.DayOfWeek == DayOfWeek.Monday,
            MaintenanceFrequency.Monthly => today.Day == 1,
            MaintenanceFrequency.Quarterly =>
                today.Day == 1 && today.Month is 1 or 4 or 7 or 10,
            MaintenanceFrequency.Semiannual =>
                today.Day == 1 && today.Month is 1 or 7,
            MaintenanceFrequency.Annual =>
                today.Day == 1 && today.Month == 1,
            _ => false,
        };
}
