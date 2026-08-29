namespace Platform.Core.Domain.Services;

/// <summary>
/// CPF check digits. Shared by registration validation and migration preflight.
/// </summary>
public static class BrazilianCpf
{
    public static bool IsValidCheckDigits(string digits)
    {
        if (digits.Length != 11 || digits.Distinct().Count() == 1)
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            sum += (digits[i] - '0') * (10 - i);
        }

        var remainder = sum % 11;
        var digit1 = remainder < 2 ? 0 : 11 - remainder;
        if (digits[9] - '0' != digit1)
        {
            return false;
        }

        sum = 0;
        for (var i = 0; i < 10; i++)
        {
            sum += (digits[i] - '0') * (11 - i);
        }

        remainder = sum % 11;
        var digit2 = remainder < 2 ? 0 : 11 - remainder;
        return digits[10] - '0' == digit2;
    }

    public static string Mask(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length < 2)
        {
            return "***.***.***-**";
        }

        return $"***.***.***-{raw[^2..]}";
    }
}
