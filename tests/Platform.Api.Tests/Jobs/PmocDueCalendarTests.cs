using Platform.Api.Jobs;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Jobs;

public sealed class PmocDueCalendarTests
{
    [Theory]
    [InlineData(MaintenanceFrequency.Daily, 2026, 9, 18, true)]
    [InlineData(MaintenanceFrequency.Weekly, 2026, 9, 14, true)]
    [InlineData(MaintenanceFrequency.Weekly, 2026, 9, 15, false)]
    [InlineData(MaintenanceFrequency.Weekly, 2026, 9, 18, false)]
    [InlineData(MaintenanceFrequency.Monthly, 2026, 9, 1, true)]
    [InlineData(MaintenanceFrequency.Monthly, 2026, 9, 2, false)]
    [InlineData(MaintenanceFrequency.Quarterly, 2026, 1, 1, true)]
    [InlineData(MaintenanceFrequency.Quarterly, 2026, 4, 1, true)]
    [InlineData(MaintenanceFrequency.Quarterly, 2026, 7, 1, true)]
    [InlineData(MaintenanceFrequency.Quarterly, 2026, 10, 1, true)]
    [InlineData(MaintenanceFrequency.Quarterly, 2026, 2, 1, false)]
    [InlineData(MaintenanceFrequency.Quarterly, 2026, 1, 2, false)]
    [InlineData(MaintenanceFrequency.Semiannual, 2026, 1, 1, true)]
    [InlineData(MaintenanceFrequency.Semiannual, 2026, 7, 1, true)]
    [InlineData(MaintenanceFrequency.Semiannual, 2026, 4, 1, false)]
    [InlineData(MaintenanceFrequency.Annual, 2026, 1, 1, true)]
    [InlineData(MaintenanceFrequency.Annual, 2026, 1, 2, false)]
    [InlineData(MaintenanceFrequency.Annual, 2026, 12, 1, false)]
    public void IsDueToday_matches_legacy_calendar(
        MaintenanceFrequency frequency,
        int year,
        int month,
        int day,
        bool expected)
    {
        Assert.Equal(expected, PmocDueCalendar.IsDueToday(frequency, new DateOnly(year, month, day)));
    }

    [Theory]
    [InlineData(MaintenanceFrequency.Daily, 2026, 9, 19, 2026, 9, 19)]
    [InlineData(MaintenanceFrequency.Weekly, 2026, 9, 14, 2026, 9, 14)]
    [InlineData(MaintenanceFrequency.Weekly, 2026, 9, 19, 2026, 9, 14)]
    [InlineData(MaintenanceFrequency.Weekly, 2026, 9, 13, 2026, 9, 7)]
    [InlineData(MaintenanceFrequency.Monthly, 2026, 9, 1, 2026, 9, 1)]
    [InlineData(MaintenanceFrequency.Monthly, 2026, 9, 19, 2026, 9, 1)]
    [InlineData(MaintenanceFrequency.Quarterly, 2026, 10, 1, 2026, 10, 1)]
    [InlineData(MaintenanceFrequency.Quarterly, 2026, 11, 15, 2026, 10, 1)]
    [InlineData(MaintenanceFrequency.Quarterly, 2026, 3, 15, 2026, 1, 1)]
    [InlineData(MaintenanceFrequency.Semiannual, 2026, 7, 1, 2026, 7, 1)]
    [InlineData(MaintenanceFrequency.Semiannual, 2026, 12, 31, 2026, 7, 1)]
    [InlineData(MaintenanceFrequency.Annual, 2026, 1, 1, 2026, 1, 1)]
    [InlineData(MaintenanceFrequency.Annual, 2026, 12, 31, 2026, 1, 1)]
    public void LastDueOnOrBefore_is_most_recent_due_date(
        MaintenanceFrequency frequency,
        int year,
        int month,
        int day,
        int dueYear,
        int dueMonth,
        int dueDay)
    {
        var today = new DateOnly(year, month, day);
        Assert.Equal(
            new DateOnly(dueYear, dueMonth, dueDay),
            PmocDueCalendar.LastDueOnOrBefore(frequency, today));
        Assert.True(PmocDueCalendar.LastDueOnOrBefore(frequency, today) <= today);
    }

    [Theory]
    [InlineData(MaintenanceFrequency.Daily, 2026, 9, 19, 2026, 9, 19)]
    [InlineData(MaintenanceFrequency.Weekly, 2026, 9, 14, 2026, 9, 14)]
    [InlineData(MaintenanceFrequency.Weekly, 2026, 9, 19, 2026, 9, 21)]
    [InlineData(MaintenanceFrequency.Monthly, 2026, 9, 1, 2026, 9, 1)]
    [InlineData(MaintenanceFrequency.Monthly, 2026, 9, 19, 2026, 10, 1)]
    [InlineData(MaintenanceFrequency.Monthly, 2026, 12, 15, 2027, 1, 1)]
    [InlineData(MaintenanceFrequency.Quarterly, 2026, 10, 1, 2026, 10, 1)]
    [InlineData(MaintenanceFrequency.Quarterly, 2026, 11, 15, 2027, 1, 1)]
    [InlineData(MaintenanceFrequency.Semiannual, 2026, 12, 31, 2027, 1, 1)]
    [InlineData(MaintenanceFrequency.Annual, 2026, 1, 1, 2026, 1, 1)]
    [InlineData(MaintenanceFrequency.Annual, 2026, 12, 31, 2027, 1, 1)]
    public void NextDueOnOrAfter_is_today_when_due_else_next_occurrence(
        MaintenanceFrequency frequency,
        int year,
        int month,
        int day,
        int dueYear,
        int dueMonth,
        int dueDay)
    {
        var today = new DateOnly(year, month, day);
        var next = PmocDueCalendar.NextDueOnOrAfter(frequency, today);
        Assert.Equal(new DateOnly(dueYear, dueMonth, dueDay), next);
        Assert.True(next >= today);
        if (PmocDueCalendar.IsDueToday(frequency, today))
        {
            Assert.Equal(today, next);
        }
    }
}
