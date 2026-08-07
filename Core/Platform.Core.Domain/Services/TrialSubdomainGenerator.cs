using System.Globalization;
using System.Text;

namespace Platform.Core.Domain.Services;

public static class TrialSubdomainGenerator
{
    private const int SubdomainLength = 4;

    /// <summary>
    /// First 4 a-z0-9 characters from the legal name (accents stripped), padded with 'x'.
    /// </summary>
    public static string SuggestBase(string legalName)
    {
        if (string.IsNullOrWhiteSpace(legalName))
        {
            return new string('x', SubdomainLength);
        }

        // FormD so combining marks can be stripped (plan: remove accents).
        var decomposed = legalName.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(SubdomainLength);

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (!char.IsLetterOrDigit(ch))
            {
                continue;
            }

            builder.Append(char.ToLowerInvariant(ch));
            if (builder.Length >= SubdomainLength)
            {
                break;
            }
        }

        while (builder.Length < SubdomainLength)
        {
            builder.Append('x');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Increments the last character (lava → lavb). On 'z'/'9' wrap, bumps the previous char.
    /// </summary>
    public static string IncrementLastChar(string subdomain)
    {
        if (string.IsNullOrWhiteSpace(subdomain))
        {
            return SuggestBase(string.Empty);
        }

        var chars = subdomain.Trim().ToLowerInvariant().ToCharArray();

        for (var i = chars.Length - 1; i >= 0; i--)
        {
            var c = chars[i];

            if (c is >= 'a' and < 'z')
            {
                chars[i] = (char)(c + 1);
                return new string(chars);
            }

            if (c is >= '0' and < '9')
            {
                chars[i] = (char)(c + 1);
                return new string(chars);
            }

            if (c == 'z' || c == '9')
            {
                chars[i] = c == 'z' ? 'a' : '0';
                continue;
            }

            // Unexpected character — replace with 'a' and stop.
            chars[i] = 'a';
            return new string(chars);
        }

        // All characters wrapped (e.g. zzzz) — grow by one.
        return new string(chars) + "a";
    }

    public static async Task<string> AllocateAsync(
        string legalName,
        Func<string, Task<bool>> isTakenAsync)
    {
        ArgumentNullException.ThrowIfNull(isTakenAsync);

        var candidate = SuggestBase(legalName);
        var guard = 0;

        while (await isTakenAsync(candidate).ConfigureAwait(false))
        {
            candidate = IncrementLastChar(candidate);
            guard++;

            if (guard > 10_000)
            {
                throw new InvalidOperationException(
                    "Unable to allocate a free trial subdomain.");
            }
        }

        return candidate;
    }
}
