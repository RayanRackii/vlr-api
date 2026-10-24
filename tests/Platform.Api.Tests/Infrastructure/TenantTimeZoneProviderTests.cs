using Platform.Api.Services.Tenancy;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.Infrastructure;

public sealed class TenantTimeZoneProviderTests
{
    [Theory]
    [InlineData("America/New_York", 3, 8, 2, 30, "2026-03-08T07:30:00Z")]
    [InlineData("America/New_York", 11, 1, 1, 30, "2026-11-01T05:30:00Z")]
    [InlineData("America/Havana", 3, 8, 0, 30, "2026-03-08T05:30:00Z")]
    [InlineData("America/Havana", 11, 1, 0, 30, "2026-11-01T04:30:00Z")]
    public void Local_time_shifts_by_the_gap_or_chooses_the_earlier_overlap_instant(
        string zoneId, int month, int day, int hour, int minute, string expected)
    {
        var instant = TenantTimeZoneProvider.AtLocal(new(2026, month, day), new(hour, minute),
            TimeZoneInfo.FindSystemTimeZoneById(zoneId));

        Assert.Equal(DateTimeOffset.Parse(expected), instant);
        Assert.Equal(TimeSpan.Zero, instant.Offset);
    }

    [Theory]
    [InlineData(3, 8)]
    [InlineData(11, 1)]
    public void Sao_Paulo_conversions_keep_the_existing_fast_path(int month, int day)
    {
        var date = new DateOnly(2026, month, day);
        var zone = BrazilTimeZone.Resolve();
        Assert.Equal(BrazilTimeZone.AtLocal(date, new(2, 30)),
            TenantTimeZoneProvider.AtLocal(date, new(2, 30), zone));
        Assert.Equal((BrazilTimeZone.StartOfCivilDay(date), BrazilTimeZone.ExclusiveEndOfCivilDay(date)),
            TenantTimeZoneProvider.CivilDay(date, zone));
    }

    [Theory]
    [InlineData(3, 7, "2026-03-07T05:00:00Z", "2026-03-08T05:00:00Z")]
    [InlineData(3, 8, "2026-03-08T05:00:00Z", "2026-03-09T04:00:00Z")]
    [InlineData(10, 31, "2026-10-31T04:00:00Z", "2026-11-01T04:00:00Z")]
    [InlineData(11, 1, "2026-11-01T04:00:00Z", "2026-11-02T05:00:00Z")]
    public void Havana_civil_day_includes_every_instant_and_stops_at_the_next_date(
        int month, int day, string start, string end)
    {
        var bounds = TenantTimeZoneProvider.CivilDay(new(2026, month, day),
            TimeZoneInfo.FindSystemTimeZoneById("America/Havana"));

        Assert.Equal(DateTimeOffset.Parse(start), bounds.Start);
        Assert.Equal(DateTimeOffset.Parse(end), bounds.EndExclusive);
    }
}
