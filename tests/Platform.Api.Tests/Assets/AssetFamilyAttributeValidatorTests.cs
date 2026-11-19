using System.Text.Json;
using Platform.Api.Modules.Assets.Services;

namespace Platform.Api.Tests.Assets;

public sealed class AssetFamilyAttributeValidatorTests
{
    private static JsonElement J(object? value) => JsonSerializer.SerializeToElement(value);

    private static Dictionary<string, JsonElement> Attrs(params (string Key, object? Value)[] items) =>
        items.ToDictionary(i => i.Key, i => J(i.Value), StringComparer.Ordinal);

    private static string Schema(params string[] fields) =>
        "{\"fields\":[" + string.Join(",", fields) + "]}";

    private static string Field(string key, string type, string extra = "") =>
        $"{{\"key\":\"{key}\",\"type\":\"{type}\",\"required\":false{extra}}}";

    private static Dictionary<string, JsonElement> Create(string schema, params (string Key, object? Value)[] items) =>
        AssetFamilyAttributeValidator.ValidateAndProject(schema, Attrs(items), existing: null, isUpdate: false);

    [Theory]
    [InlineData("256", "256")]
    [InlineData("0.020", "0.02")]
    [InlineData("-3", "-3")]
    public void Number_string_is_stored_as_a_json_number(string input, string expectedRaw)
    {
        var result = Create(Schema(Field("n", "number")), ("n", input));

        Assert.Equal(JsonValueKind.Number, result["n"].ValueKind);
        Assert.Equal(expectedRaw, result["n"].GetRawText());
    }

    [Fact]
    public void Number_json_number_is_kept_as_a_number()
    {
        var result = Create(Schema(Field("n", "number")), ("n", 256));

        Assert.Equal(JsonValueKind.Number, result["n"].ValueKind);
        Assert.Equal(256m, result["n"].GetDecimal());
    }

    [Theory]
    [InlineData("0,02")]
    [InlineData("1,000")]
    [InlineData("1e3")]
    [InlineData("abc")]
    [InlineData("1 000")]
    public void Number_rejects_non_canonical_strings(string input)
    {
        var ex = Assert.Throws<ArgumentException>(() => Create(Schema(Field("n", "number")), ("n", input)));

        Assert.Contains("'n'", ex.Message);
    }

    [Fact]
    public void Number_enforces_min_max_and_decimals()
    {
        var schema = Schema(Field("n", "number", ",\"min\":1,\"max\":10,\"decimals\":1"));

        Assert.Throws<ArgumentException>(() => Create(schema, ("n", "0.5")));
        Assert.Throws<ArgumentException>(() => Create(schema, ("n", "11")));
        Assert.Throws<ArgumentException>(() => Create(schema, ("n", "5.25")));
        Assert.Equal(5.2m, Create(schema, ("n", "5.2"))["n"].GetDecimal());
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("FALSE", false)]
    public void Boolean_is_stored_as_a_json_boolean(object input, bool expected)
    {
        var result = Create(Schema(Field("b", "boolean")), ("b", input));

        Assert.Equal(expected ? JsonValueKind.True : JsonValueKind.False, result["b"].ValueKind);
    }

    [Fact]
    public void Boolean_rejects_yes()
    {
        Assert.Throws<ArgumentException>(() => Create(Schema(Field("b", "boolean")), ("b", "yes")));
    }

    [Fact]
    public void Select_uses_the_option_spelling_and_rejects_unknown_values()
    {
        var schema = Schema(Field("s", "select", ",\"options\":[\"Grama\",\"Saibro\"]"));

        Assert.Equal("Grama", Create(schema, ("s", " grama "))["s"].GetString());
        Assert.Throws<ArgumentException>(() => Create(schema, ("s", "Areia")));
    }

    [Fact]
    public void Multiselect_stores_an_array_in_option_order()
    {
        var schema = Schema(Field("m", "multiselect", ",\"options\":[\"PLA\",\"PETG\",\"ABS\"]"));

        var fromArray = Create(schema, ("m", new[] { "abs", "PLA" }))["m"];
        var fromLegacyString = Create(schema, ("m", "PLA, petg"))["m"];

        Assert.Equal(new string?[] { "PLA", "ABS" }, fromArray.EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(new string?[] { "PLA", "PETG" }, fromLegacyString.EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Throws<ArgumentException>(() => Create(schema, ("m", "PLA, nylon")));
    }

    [Fact]
    public void Multiselect_empty_array_is_absent()
    {
        var schema = Schema(Field("m", "multiselect", ",\"options\":[\"PLA\"]"));

        var result = Create(schema, ("m", Array.Empty<string>()));

        Assert.DoesNotContain("m", result.Keys);
    }

    [Theory]
    [InlineData("https://x.y/z", true)]
    [InlineData("http://x.y", true)]
    [InlineData("ftp://x.y/z", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("/relative/path", false)]
    public void Url_accepts_only_absolute_http_and_https(string input, bool valid)
    {
        var schema = Schema(Field("u", "url"));

        if (valid)
        {
            Assert.Equal(input, Create(schema, ("u", input))["u"].GetString());
        }
        else
        {
            Assert.Throws<ArgumentException>(() => Create(schema, ("u", input)));
        }
    }

    [Theory]
    [InlineData("2026-11-19", true)]
    [InlineData("19/11/2026", false)]
    public void Date_requires_iso_format(string input, bool valid)
    {
        var schema = Schema(Field("d", "date"));

        if (valid)
        {
            Assert.Equal(input, Create(schema, ("d", input))["d"].GetString());
        }
        else
        {
            Assert.Throws<ArgumentException>(() => Create(schema, ("d", input)));
        }
    }

    [Fact]
    public void Text_enforces_max_length_and_longtext_keeps_newlines()
    {
        var textSchema = Schema(Field("t", "text", ",\"maxLength\":5"));
        var longSchema = Schema(Field("l", "longtext"));

        Assert.Throws<ArgumentException>(() => Create(textSchema, ("t", "abcdef")));
        Assert.Equal("abcde", Create(textSchema, ("t", " abcde "))["t"].GetString());
        Assert.Equal("a\nb", Create(longSchema, ("l", "a\r\nb"))["l"].GetString());
    }

    [Fact]
    public void Update_preserves_archived_and_unknown_keys_and_ignores_incoming_values_for_them()
    {
        var schema = Schema(Field("brand", "text"), Field("old", "text", ",\"archived\":true"));
        var existing = Attrs(("brand", "Acme"), ("old", "keep-me"), ("ghost", "also-keep"));

        var result = AssetFamilyAttributeValidator.ValidateAndProject(
            schema,
            Attrs(("brand", "Beta"), ("old", "overwritten"), ("ghost", "overwritten")),
            existing,
            isUpdate: true);

        Assert.Equal("Beta", result["brand"].GetString());
        Assert.Equal("keep-me", result["old"].GetString());
        Assert.Equal("also-keep", result["ghost"].GetString());
    }

    [Fact]
    public void Update_with_null_incoming_keeps_stored_values_untouched()
    {
        var schema = Schema(Field("n", "number", ",\"max\":10"));
        var existing = Attrs(("n", "999"), ("ghost", "x"));

        var result = AssetFamilyAttributeValidator.ValidateAndProject(schema, null, existing, isUpdate: true);

        Assert.Equal("999", result["n"].GetString());
        Assert.Equal("x", result["ghost"].GetString());
    }

    [Fact]
    public void Update_tolerates_an_untouched_legacy_value_and_upgrades_it_to_a_number()
    {
        var schema = Schema(Field("n", "number", ",\"max\":100"));
        var existing = Attrs(("n", "256"));

        var untouched = AssetFamilyAttributeValidator.ValidateAndProject(
            schema, Attrs(("n", "256")), existing, isUpdate: true);

        Assert.Equal(JsonValueKind.Number, untouched["n"].ValueKind);
        Assert.Equal(256m, untouched["n"].GetDecimal());
        Assert.Throws<ArgumentException>(() => AssetFamilyAttributeValidator.ValidateAndProject(
            schema, Attrs(("n", "300")), existing, isUpdate: true));
    }

    [Fact]
    public void Update_keeps_an_uncoercible_legacy_value_only_when_it_is_unchanged()
    {
        var schema = Schema(Field("n", "number"));
        var existing = Attrs(("n", "abc"));

        var kept = AssetFamilyAttributeValidator.ValidateAndProject(
            schema, Attrs(("n", "abc")), existing, isUpdate: true);

        Assert.Equal("abc", kept["n"].GetString());
        Assert.Throws<ArgumentException>(() => AssetFamilyAttributeValidator.ValidateAndProject(
            schema, Attrs(("n", "abd")), existing, isUpdate: true));
    }

    [Fact]
    public void Required_fails_on_create_and_when_clearing_an_existing_value_but_not_for_legacy_blanks()
    {
        var schema = Schema("{\"key\":\"r\",\"type\":\"text\",\"required\":true}");

        var create = Assert.Throws<ArgumentException>(() =>
            AssetFamilyAttributeValidator.ValidateAndProject(schema, Attrs(), null, isUpdate: false));
        Assert.Contains("'r'", create.Message);

        var legacy = AssetFamilyAttributeValidator.ValidateAndProject(
            schema, Attrs(("other", "x")), Attrs(("other", "x")), isUpdate: true);
        Assert.DoesNotContain("r", legacy.Keys);

        Assert.Throws<ArgumentException>(() => AssetFamilyAttributeValidator.ValidateAndProject(
            schema, Attrs(("other", "x")), Attrs(("r", "had-value")), isUpdate: true));
    }

    [Fact]
    public void Archived_fields_are_never_required_or_validated()
    {
        var schema = Schema("{\"key\":\"r\",\"type\":\"number\",\"required\":true,\"archived\":true}");

        var result = AssetFamilyAttributeValidator.ValidateAndProject(
            schema, Attrs(("r", "not-a-number")), null, isUpdate: false);

        Assert.Empty(result);
    }

    [Fact]
    public void V1_schema_json_still_parses()
    {
        var schema = AssetFamilyAttributeValidator.ParseSchema(
            """{"fields":[{"key":"capacity","type":"number","required":false,"label":"Capacidade"}]}""");

        var field = Assert.Single(schema.Fields);
        Assert.Equal("capacity", field.Key);
        Assert.Equal(1, schema.Version);
        Assert.Null(field.Options);
        Assert.False(field.Archived);
    }

    [Fact]
    public void Invalid_schema_json_throws_argument_exception()
    {
        Assert.Throws<ArgumentException>(() => AssetFamilyAttributeValidator.ParseSchema("{not json"));
    }

    [Fact]
    public void More_than_200_attributes_are_rejected()
    {
        var many = Enumerable.Range(0, 201).ToDictionary(i => $"k{i}", i => J("x"), StringComparer.Ordinal);

        Assert.Throws<ArgumentException>(() =>
            AssetFamilyAttributeValidator.ValidateAndProject(Schema(), many, null, isUpdate: false));
    }
}
