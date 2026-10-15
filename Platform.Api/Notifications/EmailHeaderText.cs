using System.Text;

namespace Platform.Api.Notifications;

/// <summary>
/// Removes characters that can split an email header or a log line.
/// Subjects and the From display name share this cut.
/// </summary>
internal static class EmailHeaderText
{
    public static string StripControls(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (IsForbidden(character))
            {
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString().Trim();
    }

    private static bool IsForbidden(char character) =>
        char.IsControl(character) || character is '\u2028' or '\u2029';
}
