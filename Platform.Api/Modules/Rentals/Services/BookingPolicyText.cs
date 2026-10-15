using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Platform.Core.Domain.Entities;

namespace Platform.Api.Modules.Rentals.Services;

internal static partial class BookingPolicyText
{
    public static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        text = WebUtility.HtmlDecode(text);
        string previous;
        do
        {
            previous = text;
            text = Tags().Replace(text, string.Empty);
        }
        while (!string.Equals(previous, text, StringComparison.Ordinal));

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character is '\n' or '\t' || !char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        text = builder.ToString().Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Length > BookingPolicy.CancellationPolicyMaxLength)
        {
            throw new ArgumentException(
                $"Cancellation policy text must be at most {BookingPolicy.CancellationPolicyMaxLength} characters.");
        }

        return text;
    }

    public static int? NormalizeDailyLimit(int? limit)
    {
        if (limit is null)
        {
            return null;
        }

        if (limit is < BookingPolicy.MinDailyActiveReservations
            or > BookingPolicy.MaxDailyActiveReservations)
        {
            throw new ArgumentException(
                $"Daily booking limit must be an integer from {BookingPolicy.MinDailyActiveReservations} to {BookingPolicy.MaxDailyActiveReservations}, or null for unlimited.");
        }

        return limit;
    }

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Tags();
}
