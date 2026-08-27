using System.Text.RegularExpressions;

namespace Platform.Core.Infrastructure.MigrationOps;

public static class SecretLogSanitizer
{
    private static readonly Regex PasswordPattern = new(
        "(?i)(Password|Pwd)\\s*=\\s*[^;\\s]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Sanitize(string? text, string? connectionString)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sanitized = text;
        if (!string.IsNullOrEmpty(connectionString))
        {
            sanitized = sanitized.Replace(connectionString, "[redacted]", StringComparison.Ordinal);
        }

        return PasswordPattern.Replace(sanitized, "$1=[redacted]");
    }
}
