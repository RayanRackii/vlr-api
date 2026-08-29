using System.Text.RegularExpressions;
using Platform.Core.Domain.Services;

namespace Platform.Api.Services.Brazil;

public static class BrazilianDocumentValidator
{
    public static string NormalizeCpf(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException("CPF is required.");
        }

        var digits = Regex.Replace(raw, @"\D", string.Empty);
        if (digits.Length != 11 || !IsValidCpfDigits(digits))
        {
            throw new ArgumentException("CPF is invalid.");
        }

        return digits;
    }

    public static string NormalizeCnpj(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException("CNPJ is required.");
        }

        var digits = Regex.Replace(raw, @"\D", string.Empty);
        if (digits.Length != 14 || !IsValidCnpjDigits(digits))
        {
            throw new ArgumentException("CNPJ is invalid.");
        }

        return digits;
    }

    public static string NormalizePostalCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException("CEP is required.");
        }

        var digits = Regex.Replace(raw, @"\D", string.Empty);
        if (digits.Length != 8)
        {
            throw new ArgumentException("CEP must have 8 digits.");
        }

        return digits;
    }

    public static string NormalizePhoneBr(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException("Phone is required.");
        }

        var digits = Regex.Replace(raw, @"\D", string.Empty);

        if (digits.StartsWith('0'))
        {
            digits = digits.TrimStart('0');
        }

        if (digits.StartsWith("55") && digits.Length >= 12)
        {
            digits = digits[2..];
        }

        // DDD (2) + number (8 or 9)
        if (digits.Length is not (10 or 11))
        {
            throw new ArgumentException("Phone must include DDD and number.");
        }

        return $"+55{digits}";
    }

    public static bool IsValidCpfDigits(string digits) => BrazilianCpf.IsValidCheckDigits(digits);

    public static bool IsValidCnpjDigits(string digits)
    {
        if (digits.Length != 14 || digits.Distinct().Count() == 1)
        {
            return false;
        }

        int[] weights1 = [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
        int[] weights2 = [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];

        var sum = 0;
        for (var i = 0; i < 12; i++)
        {
            sum += (digits[i] - '0') * weights1[i];
        }

        var remainder = sum % 11;
        var digit1 = remainder < 2 ? 0 : 11 - remainder;
        if (digits[12] - '0' != digit1)
        {
            return false;
        }

        sum = 0;
        for (var i = 0; i < 13; i++)
        {
            sum += (digits[i] - '0') * weights2[i];
        }

        remainder = sum % 11;
        var digit2 = remainder < 2 ? 0 : 11 - remainder;
        return digits[13] - '0' == digit2;
    }

    public static bool IsValidHexColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return true;
        }

        return Regex.IsMatch(color.Trim(), @"^#?[0-9A-Fa-f]{6}$");
    }
}
