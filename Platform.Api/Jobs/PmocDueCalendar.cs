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

    public static DateOnly LastDueOnOrBefore(MaintenanceFrequency frequency, DateOnly today) =>
        frequency switch
        {
            MaintenanceFrequency.Daily => today,
            MaintenanceFrequency.Weekly => today.AddDays(
                -((int)today.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7),
            MaintenanceFrequency.Monthly => new DateOnly(today.Year, today.Month, 1),
            MaintenanceFrequency.Quarterly =>
                new DateOnly(today.Year, ((today.Month - 1) / 3) * 3 + 1, 1),
            MaintenanceFrequency.Semiannual =>
                new DateOnly(today.Year, today.Month <= 6 ? 1 : 7, 1),
            MaintenanceFrequency.Annual => new DateOnly(today.Year, 1, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, null),
        };

    public static DateOnly NextDueOnOrAfter(MaintenanceFrequency frequency, DateOnly today)
    {
        if (IsDueToday(frequency, today))
        {
            return today;
        }

        var lastDue = LastDueOnOrBefore(frequency, today);
        return frequency switch
        {
            MaintenanceFrequency.Daily => lastDue.AddDays(1),
            MaintenanceFrequency.Weekly => lastDue.AddDays(7),
            MaintenanceFrequency.Monthly => lastDue.AddMonths(1),
            MaintenanceFrequency.Quarterly => lastDue.AddMonths(3),
            MaintenanceFrequency.Semiannual => lastDue.AddMonths(6),
            MaintenanceFrequency.Annual => lastDue.AddYears(1),
            _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, null),
        };
    }
}
