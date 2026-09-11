namespace Platform.Api.Notifications;

public static class PhoneLogMask
{
    public static string Last4(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return "none";
        }

        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length <= 4 ? digits : digits[^4..];
    }
}
