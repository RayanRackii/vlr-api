using System.Text;

namespace Platform.Api.Notifications;

/// <summary>
/// Builds the Resend <c>from</c> header. The mailbox stays the configured address.
/// Only the display name changes.
/// </summary>
public static class EmailFromHeader
{
    public static string Format(string? displayName, string fromEmail, string fallbackName)
    {
        var name = Sanitize(displayName);
        if (name.Length == 0)
        {
            name = Sanitize(fallbackName);
        }

        if (name.Length == 0)
        {
            name = "Rolvix";
        }

        var quoted = name.Any(c => char.IsWhiteSpace(c) || c is '"' or ',' or ';' or ':' or '\\');
        if (!quoted)
        {
            return $"{name} <{fromEmail}>";
        }

        var escaped = name
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{escaped}\" <{fromEmail}>";
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '\r' or '\n' or '\0' or '<' or '>' or '@')
            {
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString().Trim();
    }
}
