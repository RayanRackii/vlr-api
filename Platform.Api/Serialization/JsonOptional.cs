using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Api.Serialization;

/// <summary>
/// A JSON property that remembers whether the client sent it.
/// Omitted stays <see cref="IsSpecified"/> false. Explicit null is specified
/// with a null <see cref="Value"/>.
/// </summary>
public readonly struct JsonOptional<T>
    where T : struct
{
    public bool IsSpecified { get; private init; }

    public T? Value { get; private init; }

    public static JsonOptional<T> Specified(T? value) => new()
    {
        IsSpecified = true,
        Value = value,
    };

    public static implicit operator JsonOptional<T>(T? value) => Specified(value);
}

/// <summary>Distinguishes an omitted string from an explicit null or value.</summary>
public readonly struct JsonOptionalString
{
    public bool IsSpecified { get; private init; }

    public string? Value { get; private init; }

    public static JsonOptionalString Specified(string? value) => new() { IsSpecified = true, Value = value };

    public static implicit operator JsonOptionalString(string? value) => Specified(value);
}

public sealed class JsonOptionalStringConverter : JsonConverter<JsonOptionalString>
{
    public override bool HandleNull => true;

    public override JsonOptionalString Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonOptionalString.Specified(reader.TokenType == JsonTokenType.Null ? null : reader.GetString());

    public override void Write(Utf8JsonWriter writer, JsonOptionalString value, JsonSerializerOptions options)
    {
        if (value.Value is { } text) writer.WriteStringValue(text);
        else writer.WriteNullValue();
    }
}

/// <summary>
/// Reads <c>maxAdvanceBookingDays</c> so a missing property is not the same as null.
/// </summary>
public sealed class JsonOptionalInt32Converter : JsonConverter<JsonOptional<int>>
{
    public override bool HandleNull => true;

    public override JsonOptional<int> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return JsonOptional<int>.Specified(null);
        }

        return JsonOptional<int>.Specified(reader.GetInt32());
    }

    public override void Write(
        Utf8JsonWriter writer,
        JsonOptional<int> value,
        JsonSerializerOptions options)
    {
        if (!value.IsSpecified || value.Value is not int number)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteNumberValue(number);
    }
}

/// <summary>Distinguishes an omitted boolean from an explicit true or false value.</summary>
public sealed class JsonOptionalBooleanConverter : JsonConverter<JsonOptional<bool>>
{
    public override bool HandleNull => true;

    public override JsonOptional<bool> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        JsonOptional<bool>.Specified(reader.TokenType == JsonTokenType.Null ? null : reader.GetBoolean());

    public override void Write(
        Utf8JsonWriter writer,
        JsonOptional<bool> value,
        JsonSerializerOptions options)
    {
        if (value.Value is { } flag) writer.WriteBooleanValue(flag);
        else writer.WriteNullValue();
    }
}

/// <summary>Distinguishes an omitted visual revision from an explicit null assignment.</summary>
public sealed class JsonOptionalGuidConverter : JsonConverter<JsonOptional<Guid>>
{
    public override bool HandleNull => true;

    public override JsonOptional<Guid> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonOptional<Guid>.Specified(reader.TokenType == JsonTokenType.Null ? null : reader.GetGuid());

    public override void Write(Utf8JsonWriter writer, JsonOptional<Guid> value, JsonSerializerOptions options)
    {
        if (value.Value is { } id) writer.WriteStringValue(id);
        else writer.WriteNullValue();
    }
}
