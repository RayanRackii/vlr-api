namespace Platform.Core.Domain.Constants;

/// <summary>Field types for tenant-configurable B2C registration forms.</summary>
public static class RegistrationFieldTypes
{
    public const string Text = "text";
    public const string Email = "email";
    public const string Phone = "phone";
    public const string Cpf = "cpf";
    public const string Cnpj = "cnpj";
    public const string Cep = "cep";
    public const string Boolean = "boolean";
    public const string Number = "number";
    public const string Select = "select";
    public const string Photo = "photo";
    public const string Date = "date";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        Text, Email, Phone, Cpf, Cnpj, Cep, Boolean, Number, Select, Photo, Date,
    };

    /// <summary>Keys reserved for core auth fields — cannot be used as dynamic field keys.</summary>
    public static readonly HashSet<string> ReservedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "email", "password", "confirmPassword", "phone",
        "customerType", "document",
    };

    public static bool TryNormalize(string? value, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim().ToLowerInvariant();
        if (!All.Contains(trimmed))
        {
            return false;
        }

        canonical = trimmed;
        return true;
    }
}
