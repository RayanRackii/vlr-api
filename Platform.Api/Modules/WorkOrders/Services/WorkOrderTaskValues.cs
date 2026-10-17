using System.Globalization;
using System.Text.Json;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.WorkOrders.Services;

internal static class WorkOrderTaskValues
{
    public const int MaxStoredLength = 8000;

    public static bool IsFilled(WorkOrderTask task)
    {
        if (task.InputType == TaskInputType.Checkbox)
        {
            return string.Equals(task.Value, "true", StringComparison.Ordinal);
        }

        return !string.IsNullOrWhiteSpace(task.Value);
    }

    public static string? Normalize(WorkOrderTask task, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        return task.InputType switch
        {
            TaskInputType.Checkbox => NormalizeCheckbox(value),
            TaskInputType.Number => NormalizeNumber(value, task.Configuration),
            TaskInputType.SingleChoice => NormalizeChoice(value, task.Configuration),
            TaskInputType.Text => NormalizeText(value, task.Configuration),
            TaskInputType.Image => NormalizeBoundedText(value, MaxStoredLength),
            _ => throw new InvalidTaskValueException(
                InvalidTaskValueException.InvalidConfiguration,
                "Work order task input type is not supported."),
        };
    }

    private static string NormalizeCheckbox(string value)
    {
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return "true";
        }

        if (value.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return "false";
        }

        throw new InvalidTaskValueException(
            InvalidTaskValueException.InvalidCheckbox,
            "Checkbox values must be true or false.");
    }

    private static string NormalizeNumber(string value, string? configuration)
    {
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
        {
            throw new InvalidTaskValueException(
                InvalidTaskValueException.NotANumber,
                "Number values must be a decimal number.");
        }

        var (min, max) = ReadBounds(configuration);
        if ((min is decimal lower && number < lower) || (max is decimal upper && number > upper))
        {
            throw new InvalidTaskValueException(
                InvalidTaskValueException.OutOfRange,
                "Number value is outside the configured range.");
        }

        return number.ToString(CultureInfo.InvariantCulture);
    }

    private static string NormalizeChoice(string value, string? configuration)
    {
        var options = ReadOptions(configuration);
        foreach (var option in options)
        {
            if (string.Equals(option, value, StringComparison.Ordinal))
            {
                return option;
            }
        }

        throw new InvalidTaskValueException(
            InvalidTaskValueException.NotAnOption,
            "Value is not one of the configured options.");
    }

    private static string NormalizeText(string value, string? configuration)
    {
        var maxLength = ReadMaxLength(configuration);
        return NormalizeBoundedText(value, maxLength);
    }

    private static string NormalizeBoundedText(string value, int maxLength)
    {
        if (value.Length > maxLength)
        {
            throw new InvalidTaskValueException(
                InvalidTaskValueException.TextTooLong,
                "Text value is longer than the allowed length.");
        }

        return value;
    }

    private static (decimal? Min, decimal? Max) ReadBounds(string? configuration)
    {
        var root = ReadObject(configuration);
        if (root is null)
        {
            return (null, null);
        }

        return (ReadDecimal(root.Value, "min"), ReadDecimal(root.Value, "max"));
    }

    private static IReadOnlyList<string> ReadOptions(string? configuration)
    {
        var root = ReadObject(configuration);
        if (root is null
            || !root.Value.TryGetProperty("options", out var options)
            || options.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidTaskValueException(
                InvalidTaskValueException.InvalidConfiguration,
                "Single choice configuration must include an options array.");
        }

        var values = new List<string>();
        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.String)
            {
                throw new InvalidTaskValueException(
                    InvalidTaskValueException.InvalidConfiguration,
                    "Single choice options must be strings.");
            }

            var text = option.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidTaskValueException(
                    InvalidTaskValueException.InvalidConfiguration,
                    "Single choice options must be strings.");
            }

            values.Add(text);
        }

        return values;
    }

    private static int ReadMaxLength(string? configuration)
    {
        var root = ReadObject(configuration);
        if (root is null || !root.Value.TryGetProperty("maxLength", out var maxLength))
        {
            return MaxStoredLength;
        }

        if (maxLength.ValueKind != JsonValueKind.Number || !maxLength.TryGetInt32(out var limit) || limit < 1)
        {
            throw new InvalidTaskValueException(
                InvalidTaskValueException.InvalidConfiguration,
                "Text maxLength must be a positive integer.");
        }

        return Math.Min(limit, MaxStoredLength);
    }

    private static JsonElement? ReadObject(string? configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(configuration);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidTaskValueException(
                    InvalidTaskValueException.InvalidConfiguration,
                    "Task configuration must be a JSON object.");
            }

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new InvalidTaskValueException(
                InvalidTaskValueException.InvalidConfiguration,
                "Task configuration must be a JSON object.");
        }
    }

    private static decimal? ReadDecimal(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetDecimal(out var number))
        {
            throw new InvalidTaskValueException(
                InvalidTaskValueException.InvalidConfiguration,
                "Number min and max must be JSON numbers.");
        }

        return number;
    }
}
