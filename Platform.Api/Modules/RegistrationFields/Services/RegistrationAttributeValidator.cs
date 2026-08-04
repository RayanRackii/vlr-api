using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Platform.Api.Modules.RegistrationFields.Dtos;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.RegistrationFields.Services;

public static class RegistrationAttributeValidator
{
    public static Dictionary<string, string?> ValidateAndNormalize(
        IReadOnlyList<RegistrationFieldDto> schema,
        IReadOnlyDictionary<string, JsonElement>? attributes)
    {
        var incoming = attributes ?? new Dictionary<string, JsonElement>();
        var raw = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in incoming)
        {
            raw[pair.Key] = pair.Value;
        }

        var result = new Dictionary<string, string?>(StringComparer.Ordinal);

        var knownKeys = schema.Select(f => f.FieldKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in raw.Keys)
        {
            if (!knownKeys.Contains(key))
            {
                throw new ArgumentException($"Unknown registration attribute '{key}'.");
            }
        }

        foreach (var field in schema)
        {
            raw.TryGetValue(field.FieldKey, out var element);
            var hasValue = element.ValueKind is not JsonValueKind.Undefined
                and not JsonValueKind.Null
                and not JsonValueKind.False;

            // boolean false is a valid value
            if (element.ValueKind == JsonValueKind.False)
            {
                hasValue = true;
            }

            if (element.ValueKind == JsonValueKind.String
                && string.IsNullOrWhiteSpace(element.GetString()))
            {
                hasValue = false;
            }

            if (!hasValue)
            {
                if (field.IsRequired)
                {
                    throw new ArgumentException($"Attribute '{field.FieldKey}' is required.");
                }

                continue;
            }

            result[field.FieldKey] = Normalize(field, element);
        }

        return result;
    }

    private static string Normalize(RegistrationFieldDto field, JsonElement element)
    {
        return field.FieldType switch
        {
            RegistrationFieldTypes.Boolean => NormalizeBoolean(element, field.FieldKey),
            RegistrationFieldTypes.Number => NormalizeNumber(element, field.FieldKey),
            RegistrationFieldTypes.Cpf => NormalizeCpf(element, field.FieldKey),
            RegistrationFieldTypes.Cep => NormalizeCep(element, field.FieldKey),
            RegistrationFieldTypes.Phone => NormalizePhone(element, field.FieldKey),
            RegistrationFieldTypes.Email => NormalizeEmail(element, field.FieldKey),
            RegistrationFieldTypes.Select => NormalizeSelect(element, field),
            RegistrationFieldTypes.Photo => NormalizePhoto(element, field.FieldKey),
            RegistrationFieldTypes.Date => NormalizeDate(element, field.FieldKey),
            _ => NormalizeText(element, field.FieldKey),
        };
    }

    private static string NormalizeText(JsonElement element, string key)
    {
        var value = element.ValueKind == JsonValueKind.String
            ? element.GetString()?.Trim()
            : element.ToString();

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Attribute '{key}' is invalid.");
        }

        return value;
    }

    private static string NormalizeBoolean(JsonElement element, string key)
    {
        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return element.GetBoolean() ? "true" : "false";
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var s = element.GetString()?.Trim().ToLowerInvariant();
            if (s is "true" or "false" or "1" or "0" or "sim" or "nao" or "não")
            {
                return s is "true" or "1" or "sim" ? "true" : "false";
            }
        }

        throw new ArgumentException($"Attribute '{key}' must be a boolean.");
    }

    private static string NormalizeNumber(JsonElement element, string key)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.GetRawText();
        }

        if (element.ValueKind == JsonValueKind.String
            && decimal.TryParse(
                element.GetString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var number))
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }

        throw new ArgumentException($"Attribute '{key}' must be a number.");
    }

    private static string NormalizeCpf(JsonElement element, string key)
    {
        var digits = OnlyDigits(NormalizeText(element, key));
        if (digits.Length != 11)
        {
            throw new ArgumentException($"Attribute '{key}' must be a valid CPF.");
        }

        return digits;
    }

    private static string NormalizeCep(JsonElement element, string key)
    {
        var digits = OnlyDigits(NormalizeText(element, key));
        if (digits.Length != 8)
        {
            throw new ArgumentException($"Attribute '{key}' must be a valid CEP.");
        }

        return digits;
    }

    private static string NormalizePhone(JsonElement element, string key)
    {
        var digits = OnlyDigits(NormalizeText(element, key));
        if (digits.Length is < 10 or > 13)
        {
            throw new ArgumentException($"Attribute '{key}' must be a valid phone.");
        }

        return digits;
    }

    private static string NormalizeEmail(JsonElement element, string key)
    {
        var value = NormalizeText(element, key).ToLowerInvariant();
        if (!value.Contains('@') || value.Length < 5)
        {
            throw new ArgumentException($"Attribute '{key}' must be a valid email.");
        }

        return value;
    }

    private static string NormalizeSelect(JsonElement element, RegistrationFieldDto field)
    {
        var value = NormalizeText(element, field.FieldKey);
        if (field.Options is { Count: > 0 }
            && !field.Options.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Attribute '{field.FieldKey}' must be one of the allowed options.");
        }

        return value;
    }

    private static string NormalizePhoto(JsonElement element, string key)
    {
        var value = NormalizeText(element, key);
        if (value.Length is < 32 or > 400_000)
        {
            throw new ArgumentException($"Attribute '{key}' photo is invalid or too large.");
        }

        return value;
    }

    private static string NormalizeDate(JsonElement element, string key)
    {
        var value = NormalizeText(element, key);
        if (!DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new ArgumentException($"Attribute '{key}' must be a date (YYYY-MM-DD).");
        }

        return value;
    }

    private static string OnlyDigits(string value) =>
        Regex.Replace(value, @"\D", string.Empty);
}
