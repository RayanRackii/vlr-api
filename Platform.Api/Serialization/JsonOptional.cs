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
