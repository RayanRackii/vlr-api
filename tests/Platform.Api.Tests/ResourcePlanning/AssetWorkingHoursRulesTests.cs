using Platform.Api.Modules.ResourcePlanning.Services;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.ResourcePlanning;

/// <summary>
/// Pure rules: the three hours policies, multi-day intervals, split days and DST days. Hours reject.
/// America/New_York is used for DST (2026-03-08 spring forward, 2026-11-01 fall back); 2026-03-09
/// is a Monday.
/// </summary>
public sealed class AssetWorkingHoursRulesTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static readonly WorkingHoursWindow[] WeekdaysEightToSix =
    [
        Window(DayOfWeek.Monday, "08:00", "18:00"),
        Window(DayOfWeek.Tuesday, "08:00", "18:00"),
        Window(DayOfWeek.Wednesday, "08:00", "18:00"),
        Window(DayOfWeek.Thursday, "08:00", "18:00"),
        Window(DayOfWeek.Friday, "08:00", "18:00"),
    ];

    // ---- Anytime

    [Fact]
    public void Anytime_allows_any_interval_even_with_no_windows()
    {
        Assert.Null(Check(AssetHoursPolicy.Anytime, [], Local(2026, 3, 8, 3, 0), Local(2026, 3, 12, 3, 0)));
    }

    [Fact]
    public void Empty_inverted_and_too_long_intervals_are_invalid_for_every_policy()
    {
        foreach (var policy in Enum.GetValues<AssetHoursPolicy>())
        {
            var at = Local(2026, 3, 9, 9, 0);
            Assert.Equal(AssetWorkingHoursRules.InvalidInterval, Check(policy, WeekdaysEightToSix, at, at));
            Assert.Equal(AssetWorkingHoursRules.InvalidInterval, Check(policy, WeekdaysEightToSix, at, at.AddMinutes(-1)));
            Assert.Equal(
                AssetWorkingHoursRules.InvalidInterval,
                Check(policy, WeekdaysEightToSix, at, at.AddDays(AssetWorkingHoursRules.MaxIntervalDays).AddMinutes(1)));
        }
    }

    // ---- StartWithinHours (the default policy)

    [Fact]
    public void StartWithinHours_allows_a_job_that_starts_open_and_finishes_overnight_or_over_the_weekend()
    {
        // The 3D printer case: starts Friday 17:00, finishes Monday morning, unattended.
        Assert.Null(Check(
            AssetHoursPolicy.StartWithinHours,
            WeekdaysEightToSix,
            Local(2026, 3, 13, 17, 0),
            Local(2026, 3, 16, 9, 0)));
    }

    [Theory]
    [InlineData(9, 0, true)]
    [InlineData(8, 0, true)]
    [InlineData(17, 59, true)]
    [InlineData(18, 0, false)]
    [InlineData(7, 59, false)]
    [InlineData(23, 0, false)]
    public void StartWithinHours_start_is_inclusive_at_open_and_exclusive_at_close(int hour, int minute, bool allowed)
    {
        var result = Check(
            AssetHoursPolicy.StartWithinHours,
            WeekdaysEightToSix,
            Local(2026, 3, 9, hour, minute),
            Local(2026, 3, 11, 12, 0));

        Assert.Equal(allowed ? null : AssetWorkingHoursRules.StartOutsideHours, result);
    }

    [Fact]
    public void StartWithinHours_rejects_a_start_on_a_closed_day_and_when_there_are_no_windows()
    {
        // 2026-03-14 is a Saturday.
        Assert.Equal(
            AssetWorkingHoursRules.StartOutsideHours,
            Check(AssetHoursPolicy.StartWithinHours, WeekdaysEightToSix, Local(2026, 3, 14, 10, 0), Local(2026, 3, 14, 11, 0)));
        Assert.Equal(
            AssetWorkingHoursRules.StartOutsideHours,
            Check(AssetHoursPolicy.StartWithinHours, [], Local(2026, 3, 9, 10, 0), Local(2026, 3, 9, 11, 0)));
    }

    // ---- WithinHours

    [Theory]
    [InlineData(8, 0, 18, 0, true)]
    [InlineData(9, 0, 17, 0, true)]
    [InlineData(9, 0, 18, 1, false)]
    [InlineData(7, 59, 12, 0, false)]
    public void WithinHours_requires_the_whole_job_inside_the_window(int sh, int sm, int eh, int em, bool allowed)
    {
        var result = Check(
            AssetHoursPolicy.WithinHours,
            WeekdaysEightToSix,
            Local(2026, 3, 9, sh, sm),
            Local(2026, 3, 9, eh, em));

        Assert.Equal(allowed ? null : AssetWorkingHoursRules.OutsideHours, result);
    }

    [Fact]
    public void WithinHours_rejects_a_job_that_runs_overnight_when_the_asset_closes()
    {
        Assert.Equal(
            AssetWorkingHoursRules.OutsideHours,
            Check(AssetHoursPolicy.WithinHours, WeekdaysEightToSix, Local(2026, 3, 9, 17, 0), Local(2026, 3, 10, 9, 0)));
    }

    // ---- multi-day

    [Fact]
    public void WithinHours_chains_days_that_are_open_until_midnight_and_from_midnight()
    {
        var allDay = new[]
        {
            Window(DayOfWeek.Monday, "00:00", "23:59"),
            Window(DayOfWeek.Tuesday, "00:00", "23:59"),
            Window(DayOfWeek.Wednesday, "00:00", "23:59"),
        };

        Assert.Null(Check(AssetHoursPolicy.WithinHours, allDay, Local(2026, 3, 9, 10, 0), Local(2026, 3, 11, 10, 0)));
        Assert.Null(Check(AssetHoursPolicy.WithinHours, allDay, Local(2026, 3, 9, 0, 0), Local(2026, 3, 12, 0, 0)));
        // Thursday has no window: the job cannot run past Wednesday midnight.
        Assert.Equal(
            AssetWorkingHoursRules.OutsideHours,
            Check(AssetHoursPolicy.WithinHours, allDay, Local(2026, 3, 11, 10, 0), Local(2026, 3, 12, 10, 0)));
    }

    [Fact]
    public void WithinHours_multi_day_fails_when_a_middle_day_is_closed()
    {
        var gapOnTuesday = new[]
        {
            Window(DayOfWeek.Monday, "00:00", "23:59"),
            Window(DayOfWeek.Wednesday, "00:00", "23:59"),
        };

        Assert.Equal(
            AssetWorkingHoursRules.OutsideHours,
            Check(AssetHoursPolicy.WithinHours, gapOnTuesday, Local(2026, 3, 9, 10, 0), Local(2026, 3, 11, 10, 0)));
    }

    // ---- split day

    [Fact]
    public void Split_day_keeps_the_break_closed_for_WithinHours_and_for_the_start()
    {
        var lunch = new[]
        {
            Window(DayOfWeek.Monday, "08:00", "12:00"),
            Window(DayOfWeek.Monday, "13:00", "17:00"),
        };

        Assert.Null(Check(AssetHoursPolicy.WithinHours, lunch, Local(2026, 3, 9, 8, 0), Local(2026, 3, 9, 12, 0)));
        Assert.Null(Check(AssetHoursPolicy.WithinHours, lunch, Local(2026, 3, 9, 13, 0), Local(2026, 3, 9, 17, 0)));
        Assert.Equal(
            AssetWorkingHoursRules.OutsideHours,
            Check(AssetHoursPolicy.WithinHours, lunch, Local(2026, 3, 9, 11, 0), Local(2026, 3, 9, 14, 0)));

        Assert.Equal(
            AssetWorkingHoursRules.StartOutsideHours,
            Check(AssetHoursPolicy.StartWithinHours, lunch, Local(2026, 3, 9, 12, 30), Local(2026, 3, 9, 15, 0)));
        Assert.Null(Check(AssetHoursPolicy.StartWithinHours, lunch, Local(2026, 3, 9, 13, 0), Local(2026, 3, 9, 20, 0)));
        Assert.Null(Check(AssetHoursPolicy.StartWithinHours, lunch, Local(2026, 3, 9, 11, 59), Local(2026, 3, 9, 14, 0)));
    }

    [Fact]
    public void Touching_and_overlapping_windows_merge_into_one_continuous_window()
    {
        var touching = new[]
        {
            Window(DayOfWeek.Monday, "08:00", "12:00"),
            Window(DayOfWeek.Monday, "12:00", "17:00"),
            Window(DayOfWeek.Monday, "16:00", "20:00"),
        };

        Assert.Null(Check(AssetHoursPolicy.WithinHours, touching, Local(2026, 3, 9, 8, 0), Local(2026, 3, 9, 20, 0)));
    }

    // ---- DST days

    [Fact]
    public void Spring_forward_day_is_judged_on_real_instants()
    {
        // Sunday 2026-03-08: 01:00 EST (06:00Z) to 05:00 EDT (09:00Z) is three real hours.
        var sunday = new[] { Window(DayOfWeek.Sunday, "01:00", "05:00") };
        var open = Utc("2026-03-08T06:00:00Z");
        var close = Utc("2026-03-08T09:00:00Z");

        Assert.Null(AssetWorkingHoursRules.Validate(AssetHoursPolicy.WithinHours, sunday, NewYork, open, close));
        Assert.Equal(
            AssetWorkingHoursRules.OutsideHours,
            AssetWorkingHoursRules.Validate(AssetHoursPolicy.WithinHours, sunday, NewYork, open, close.AddSeconds(1)));
        // 07:30Z is 03:30 EDT, inside the window even though the clock skipped 02:00 to 03:00.
        Assert.Null(AssetWorkingHoursRules.Validate(
            AssetHoursPolicy.StartWithinHours, sunday, NewYork, Utc("2026-03-08T07:30:00Z"), Utc("2026-03-08T12:00:00Z")));
    }

    [Fact]
    public void A_window_inside_the_spring_forward_gap_shifts_forward_by_the_gap()
    {
        // 02:15-02:45 does not exist on 2026-03-08; both ends move forward one hour: 03:15-03:45 EDT.
        var inGap = new[] { Window(DayOfWeek.Sunday, "02:15", "02:45") };

        Assert.Null(AssetWorkingHoursRules.Validate(
            AssetHoursPolicy.WithinHours, inGap, NewYork, Utc("2026-03-08T07:15:00Z"), Utc("2026-03-08T07:45:00Z")));
        Assert.Equal(
            AssetWorkingHoursRules.StartOutsideHours,
            AssetWorkingHoursRules.Validate(
                AssetHoursPolicy.StartWithinHours, inGap, NewYork, Utc("2026-03-08T06:30:00Z"), Utc("2026-03-08T08:30:00Z")));
    }

    [Fact]
    public void A_window_that_would_end_before_it_starts_after_the_gap_shift_is_never_open()
    {
        // 02:30-03:00: the open time shifts to 03:30 EDT while 03:00 EDT is a valid close, so the
        // window is empty and must not open anything.
        var inverted = new[] { Window(DayOfWeek.Sunday, "02:30", "03:00") };

        foreach (var start in new[] { "2026-03-08T07:00:00Z", "2026-03-08T07:15:00Z", "2026-03-08T07:30:00Z", "2026-03-08T07:45:00Z" })
        {
            Assert.Equal(
                AssetWorkingHoursRules.StartOutsideHours,
                AssetWorkingHoursRules.Validate(
                    AssetHoursPolicy.StartWithinHours, inverted, NewYork, Utc(start), Utc("2026-03-08T12:00:00Z")));
        }
    }

    [Fact]
    public void Fall_back_day_window_covers_the_repeated_hour()
    {
        // Sunday 2026-11-01: 00:00 EDT (04:00Z) to 04:00 EST (09:00Z) is five real hours.
        var sunday = new[] { Window(DayOfWeek.Sunday, "00:00", "04:00") };

        Assert.Null(AssetWorkingHoursRules.Validate(
            AssetHoursPolicy.WithinHours, sunday, NewYork, Utc("2026-11-01T04:00:00Z"), Utc("2026-11-01T09:00:00Z")));
        Assert.Equal(
            AssetWorkingHoursRules.OutsideHours,
            AssetWorkingHoursRules.Validate(
                AssetHoursPolicy.WithinHours, sunday, NewYork, Utc("2026-11-01T04:00:00Z"), Utc("2026-11-01T09:00:01Z")));
        // 06:30Z is the second 01:30 (EST): still inside.
        Assert.Null(AssetWorkingHoursRules.Validate(
            AssetHoursPolicy.StartWithinHours, sunday, NewYork, Utc("2026-11-01T06:30:00Z"), Utc("2026-11-01T12:00:00Z")));
        // 09:00Z is 04:00 EST: closed.
        Assert.Equal(
            AssetWorkingHoursRules.StartOutsideHours,
            AssetWorkingHoursRules.Validate(
                AssetHoursPolicy.StartWithinHours, sunday, NewYork, Utc("2026-11-01T09:00:00Z"), Utc("2026-11-01T12:00:00Z")));
    }

    [Fact]
    public void Chained_all_day_windows_run_across_a_dst_change()
    {
        var everyDay = Enum.GetValues<DayOfWeek>().Select(day => Window(day, "00:00", "23:59")).ToArray();

        // Saturday noon to Monday noon spans the 23 hour day of 2026-03-08.
        Assert.Null(Check(AssetHoursPolicy.WithinHours, everyDay, Local(2026, 3, 7, 12, 0), Local(2026, 3, 9, 12, 0)));
        // Saturday noon to Monday noon across the 25 hour day of 2026-11-01.
        Assert.Null(Check(AssetHoursPolicy.WithinHours, everyDay, Local(2026, 10, 31, 12, 0), Local(2026, 11, 2, 12, 0)));
    }

    [Fact]
    public void Interval_dates_follow_the_tenant_zone_not_utc()
    {
        // 02:00Z on Tuesday is still Monday 22:00 in New York: not inside Monday 08:00-18:00.
        var mondayOnly = new[] { Window(DayOfWeek.Monday, "08:00", "18:00") };

        Assert.Equal(
            AssetWorkingHoursRules.StartOutsideHours,
            AssetWorkingHoursRules.Validate(
                AssetHoursPolicy.StartWithinHours, mondayOnly, NewYork, Utc("2026-03-10T02:00:00Z"), Utc("2026-03-10T03:00:00Z")));
        // 13:00Z on Monday is 09:00 EDT: inside.
        Assert.Null(AssetWorkingHoursRules.Validate(
            AssetHoursPolicy.StartWithinHours, mondayOnly, NewYork, Utc("2026-03-09T13:00:00Z"), Utc("2026-03-09T14:00:00Z")));
    }

    // ---- helpers

    private static string? Check(
        AssetHoursPolicy policy,
        IReadOnlyCollection<WorkingHoursWindow> windows,
        DateTimeOffset start,
        DateTimeOffset end) =>
        AssetWorkingHoursRules.Validate(policy, windows, NewYork, start, end);

    private static WorkingHoursWindow Window(DayOfWeek day, string open, string close) =>
        new(day, TimeOnly.Parse(open), TimeOnly.Parse(close));

    private static DateTimeOffset Utc(string iso) => DateTimeOffset.Parse(iso).ToUniversalTime();

    /// <summary>A local New York wall-clock time as an instant (callers avoid the DST gap).</summary>
    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute)
    {
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, NewYork), TimeSpan.Zero);
    }
}
