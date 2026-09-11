using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.Infrastructure;

public sealed class BrazilTimeZoneTests
{
    private static readonly DateOnly Date = new(2026, 9, 10);

    [Theory]
    [InlineData(10, 0, 2026, 9, 10, 13, 0)]
    [InlineData(11, 0, 2026, 9, 10, 14, 0)]
    [InlineData(0, 0, 2026, 9, 10, 3, 0)]
    [InlineData(22, 0, 2026, 9, 11, 1, 0)]
    public void AtLocal_converts_sao_paulo_civil_clock_to_utc_instant(
        int civilHour,
        int civilMinute,
        int utcYear,
        int utcMonth,
        int utcDay,
        int utcHour,
        int utcMinute)
    {
        var instant = BrazilTimeZone.AtLocal(Date, new TimeOnly(civilHour, civilMinute));

        Assert.Equal(TimeSpan.Zero, instant.Offset);
        Assert.Equal(
            new DateTime(utcYear, utcMonth, utcDay, utcHour, utcMinute, 0, DateTimeKind.Utc),
            instant.UtcDateTime);
    }

    [Fact]
    public void Civil_day_bounds_are_inclusive_start_and_exclusive_next_midnight()
    {
        var start = BrazilTimeZone.StartOfCivilDay(Date);
        var end = BrazilTimeZone.ExclusiveEndOfCivilDay(Date);

        Assert.Equal(BrazilTimeZone.AtLocal(Date, TimeOnly.MinValue), start);
        Assert.Equal(BrazilTimeZone.AtLocal(Date.AddDays(1), TimeOnly.MinValue), end);
        Assert.Equal(BrazilTimeZone.AtLocal(Date, new TimeOnly(22, 0)), end.AddHours(-2));
    }

    [Fact]
    public void FormatCivilDateTime_uses_sao_paulo_clock_without_utc_offset_text()
    {
        var instant = BrazilTimeZone.AtLocal(Date, new TimeOnly(10, 0));
        var formatted = BrazilTimeZone.FormatCivilDateTime(instant);

        Assert.Equal("10/09/2026 10:00", formatted);
        Assert.DoesNotContain("Z", formatted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-03", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("+00", formatted, StringComparison.Ordinal);
    }
}
