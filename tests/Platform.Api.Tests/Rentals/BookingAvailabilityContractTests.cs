using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Platform.Api.Modules.CustomerAuth.Dtos;
using Platform.Api.Modules.Rentals.Dtos;

namespace Platform.Api.Tests.Rentals;

public sealed class BookingAvailabilityContractTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string Root = System.IO.Path.Combine(AppContext.BaseDirectory, "Contracts", "booking");

    [Theory]
    [InlineData("daily-states.json")] [InlineData("daily-mixed-horizons.json")] [InlineData("daily-empty.json")]
    public void Public_fixtures_match_schema_and_typed_contract(string file)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(Root, "fixtures", file)));
        using var schema = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(Root, "daily-availability.v1.schema.json")));
        Validate(fixture.RootElement, schema.RootElement, schema.RootElement);
        var dto = fixture.RootElement.Deserialize<DailyAvailabilityResponseDto>(Json);
        Assert.NotNull(dto);
        Assert.Equal(TimeSpan.FromSeconds(60), dto.QuoteExpiresAt - dto.ServerNow);
        using var roundTrip = JsonDocument.Parse(JsonSerializer.Serialize(dto, Json));
        Validate(roundTrip.RootElement, schema.RootElement, schema.RootElement);
        Assert.True(JsonElement.DeepEquals(fixture.RootElement, roundTrip.RootElement));
    }

    [Fact]
    public async Task Actual_controller_response_matches_the_published_schema()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        using var host = DailyAvailabilityHttpTests.StartHost(h);
        var response = await host.GetTestClient()
            .GetAsync("/api/public/tenants/example/availability/days/2026-10-21");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        using var schema = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(Root, "daily-availability.v1.schema.json")));
        Validate(body.RootElement, schema.RootElement, schema.RootElement);
    }

    [Fact]
    public void Legacy_booking_shapes_remain_readable_without_new_required_fields()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(Root, "fixtures", "legacy-contracts.json")));
        var create = fixture.RootElement.GetProperty("createReservation").Deserialize<CreateReservationRequestDto>(Json);
        Assert.NotNull(create);
        Assert.Equal(2, Assert.Single(create.Items).Quantity);
        var book = fixture.RootElement.GetProperty("bookSlot").Deserialize<BookSlotRequestDto>(Json);
        Assert.NotNull(book);
        Assert.Equal(1, book.Quantity);
        Assert.Empty(fixture.RootElement.GetProperty("legacyDay").Deserialize<DayScheduleResponseDto>(Json)!.Slots);
        Assert.False(fixture.RootElement.GetProperty("branding").Deserialize<TenantBrandingResponseDto>(Json)!.NewBookingUiEnabled);
        Assert.Equal(new[] { "unitId", "date", "startTime", "endTime", "items" },
            JsonSerializer.SerializeToElement(create, Json).EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "slotId", "unitId", "quantity" },
            JsonSerializer.SerializeToElement(book, Json).EnumerateObject().Select(p => p.Name));
    }

    // Structural contract assertions use the published schema itself; format checks are also
    // available through any standard Draft 2020-12 JSON Schema validator.
    private static void Validate(JsonElement value, JsonElement schema, JsonElement root)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var resolved = root;
            foreach (var part in reference.GetString()![2..].Split('/')) resolved = resolved.GetProperty(part);
            Validate(value, resolved, root);
            return;
        }
        if (schema.TryGetProperty("type", out var type))
        {
            var allowed = type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Select(t => t.GetString()).ToArray() : [type.GetString()];
            var actual = value.ValueKind switch
            { JsonValueKind.Object => "object", JsonValueKind.Array => "array", JsonValueKind.String => "string",
                JsonValueKind.Number => value.TryGetInt64(out _) ? "integer" : "number",
                JsonValueKind.True or JsonValueKind.False => "boolean", _ => "null" };
            Assert.True(allowed.Contains(actual) || actual == "integer" && allowed.Contains("number"), $"Expected {type}, got {actual}.");
        }
        if (schema.TryGetProperty("enum", out var choices))
            Assert.Contains(choices.EnumerateArray(), choice => JsonElement.DeepEquals(choice, value));
        if (schema.TryGetProperty("const", out var constant)) Assert.True(JsonElement.DeepEquals(constant, value));
        if (value.ValueKind == JsonValueKind.Number && schema.TryGetProperty("minimum", out var minimum))
            Assert.True(value.GetDecimal() >= minimum.GetDecimal());
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var required in schema.GetProperty("required").EnumerateArray())
                Assert.True(value.TryGetProperty(required.GetString()!, out _), $"Missing {required}.");
            foreach (var property in value.EnumerateObject())
            {
                Assert.True(schema.GetProperty("properties").TryGetProperty(property.Name, out var child), $"Unexpected {property.Name}.");
                Validate(property.Value, child, root);
            }
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (schema.TryGetProperty("maxItems", out var max)) Assert.True(value.GetArrayLength() <= max.GetInt32());
            foreach (var item in value.EnumerateArray()) Validate(item, schema.GetProperty("items"), root);
        }
    }
}
