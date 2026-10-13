using System.Globalization;

namespace Platform.Api.Authentication;

public static class CustomerPasswordStamp
{
    public const string ClaimType = "pwd_at";

    public static string Format(DateTimeOffset changedAt) =>
        changedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    public static bool Matches(string? claim, DateTimeOffset changedAt) =>
        long.TryParse(claim, NumberStyles.Integer, CultureInfo.InvariantCulture, out var claimMs)
        && claimMs == changedAt.ToUnixTimeMilliseconds();
}
