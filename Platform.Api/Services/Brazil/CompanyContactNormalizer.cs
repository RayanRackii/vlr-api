using System.Net.Mail;

namespace Platform.Api.Services.Brazil;

/// <summary>Shared company contact validation (tenant-scoped company profile and platform-admin tenant edit).</summary>
public static class CompanyContactNormalizer
{
    /// <summary>Returns the trimmed e-mail, or null when blank. Throws <see cref="ArgumentException"/> when invalid.</summary>
    public static string? NormalizeEmail(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var email = raw.Trim();
        try
        {
            var parsed = new MailAddress(email);
            if (!string.Equals(parsed.Address, email, StringComparison.Ordinal) || !parsed.Host.Contains('.'))
            {
                throw new ArgumentException("E-mail is invalid.");
            }
        }
        catch (FormatException)
        {
            throw new ArgumentException("E-mail is invalid.");
        }

        return email;
    }

    /// <summary>Returns the normalized phone, or null when blank.</summary>
    public static string? NormalizePhone(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : BrazilianDocumentValidator.NormalizePhoneBr(raw);

    /// <summary>Returns the 8-digit CEP, or null when blank.</summary>
    public static string? NormalizePostalCode(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : BrazilianDocumentValidator.NormalizePostalCode(raw);
}
