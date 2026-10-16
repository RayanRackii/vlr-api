using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Api.Modules.Rentals.Controllers;
using Platform.Api.Serialization;
using Swashbuckle.AspNetCore;

namespace Platform.Api.Tests.Rentals;

public sealed class BookingHorizonOpenApiTests
{
    [Fact]
    public async Task MaxAdvanceBookingDays_is_a_nullable_integer_on_policy_and_resource_updates()
    {
        using var host = await StartSwaggerHostAsync();
        var client = host.GetTestClient();
        var response = await client.GetAsync("/swagger/v1/swagger.json");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);

        using var document = JsonDocument.Parse(body);
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        AssertNullableInteger(schemas, "UpsertBookingPolicyRequestDto");
        AssertNullableInteger(schemas, "UpdateRentableRequest");
        Assert.False(ContainsPresenceObject(schemas), body);
    }

    private static void AssertNullableInteger(JsonElement schemas, string schemaName)
    {
        Assert.True(schemas.TryGetProperty(schemaName, out var schema), schemaName);
        Assert.True(
            schema.GetProperty("properties").TryGetProperty("maxAdvanceBookingDays", out var property),
            schema.GetRawText());
        Assert.True(IsNullableInteger(property), property.GetRawText());
        if (schema.TryGetProperty("required", out var required))
        {
            Assert.DoesNotContain(
                required.EnumerateArray().Select(item => item.GetString()),
                name => string.Equals(name, "maxAdvanceBookingDays", StringComparison.Ordinal));
        }
    }

    private static bool IsNullableInteger(JsonElement property)
    {
        if (property.TryGetProperty("$ref", out _)
            || property.TryGetProperty("properties", out _))
        {
            return false;
        }

        if (!property.TryGetProperty("type", out var type))
        {
            return false;
        }

        if (type.ValueKind == JsonValueKind.Array)
        {
            var names = type.EnumerateArray().Select(item => item.GetString()).ToHashSet(StringComparer.Ordinal);
            return names.Contains("integer") && names.Contains("null");
        }

        var nullable = property.TryGetProperty("nullable", out var nullableValue)
            && nullableValue.ValueKind == JsonValueKind.True;
        return type.GetString() == "integer" && nullable;
    }

    private static bool ContainsPresenceObject(JsonElement schemas)
    {
        foreach (var schema in schemas.EnumerateObject())
        {
            if (!schema.Value.TryGetProperty("properties", out var properties))
            {
                continue;
            }

            if (properties.TryGetProperty("isSpecified", out _)
                && properties.TryGetProperty("value", out _))
            {
                return true;
            }
        }

        return false;
    }

    private static Task<IHost> StartSwaggerHostAsync()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddControllers()
                        .ConfigureApplicationPartManager(manager =>
                        {
                            manager.ApplicationParts.Clear();
                            foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
                            {
                                manager.FeatureProviders.Remove(provider);
                            }

                            manager.ApplicationParts.Add(new AssemblyPart(typeof(BookingPolicyController).Assembly));
                            manager.FeatureProviders.Add(new HorizonControllers());
                        });
                    services.AddEndpointsApiExplorer();
                    services.AddSwaggerGen(JsonOptionalOpenApi.Configure);
                });
                web.Configure(app =>
                {
                    app.UseSwagger();
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .Start();
        return Task.FromResult(host);
    }

    private sealed class HorizonControllers : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo)
        {
            var type = typeInfo.AsType();
            return type == typeof(BookingPolicyController) || type == typeof(RentalAssetsController);
        }
    }
}
