using System.Text;

namespace Platform.Api.Notifications;

/// <summary>
/// Encodes HTML special characters and leaves letters outside ASCII intact.
/// The documents already declare UTF-8, so accented Portuguese stays readable.
/// </summary>
public static class EmailHtml
{
    public static string Encode(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            switch (character)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '"':
                    builder.Append("&quot;");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }
}
