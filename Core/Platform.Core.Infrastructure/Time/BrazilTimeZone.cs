namespace Platform.Core.Infrastructure.Time;

/// <summary>
/// Platform Brazil wall clock (not per-tenant). Windows uses
/// "E. South America Standard Time"; Linux uses "America/Sao_Paulo".
/// </summary>
public static class BrazilTimeZone
{
    public static TimeZoneInfo Resolve()
    {
        string[] candidateIds =
        [
            "E. South America Standard Time",
            "America/Sao_Paulo",
        ];

        foreach (var timeZoneId in candidateIds)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        throw new InvalidOperationException(
            "Could not resolve Brazil time zone. Tried: E. South America Standard Time, America/Sao_Paulo.");
    }

    public static DateOnly GetToday(TimeProvider? timeProvider = null)
    {
        var utcNow = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return GetCivilDate(utcNow);
    }

    public static DateOnly GetCivilDate(DateTimeOffset utcNow)
    {
        var brazilTimeZone = Resolve();
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow.UtcDateTime, brazilTimeZone);
        return DateOnly.FromDateTime(localNow);
    }

    public static DateTimeOffset AtLocal(DateOnly date, TimeOnly time)
    {
        var brazilTimeZone = Resolve();
        var localUnspecified = date.ToDateTime(time, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(localUnspecified, brazilTimeZone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }
}
