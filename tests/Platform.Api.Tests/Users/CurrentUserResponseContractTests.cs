using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Api.Modules.Users.Controllers;
using Swashbuckle.AspNetCore;
using Platform.Api.Modules.Users.Dtos;

namespace Platform.Api.Tests.Users;

public sealed class CurrentUserResponseContractTests
{
    private static readonly string[] WireProperties =
    [
        "id",
        "fullName",
        "email",
        "role",
        "tenantId",
        "activeModules",
        "activeAssetFamilies",
        "isTrial",
        "trialEndsAt",
        "trialPurgeAt",
        "isTrialReadOnly",
        "notificationsEmailOnly",
        "roles",
        "permissions",
        "tenantTradeName",
        "tenantLegalName",
        "phone",
        "timeZoneId",
    ];

    [Fact]
    public void Me_payload_keeps_existing_fields_and_adds_nullable_tenant_names()
    {
        var json = JsonSerializer.Serialize(
            new CurrentUserResponse(
                null,
                "Ada",
                "ada@studio.test",
                ApplicationRoles.User,
                null,
                [],
                [],
                false,
                null,
                null,
                false,
                false,
                [],
                [],
                null,
                null),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var doc = JsonDocument.Parse(json);
        var names = doc.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(WireProperties, names);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("tenantTradeName").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("tenantLegalName").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("phone").ValueKind);

        var named = JsonSerializer.Serialize(
            new CurrentUserResponse(
                Guid.NewGuid(),
                "Ada",
                "ada@studio.test",
                ApplicationRoles.Admin,
                Guid.NewGuid(),
                ["rentals"],
                [],
                false,
                null,
                null,
                false,
                false,
                [],
                [],
                "Estúdio Norte",
                "Estúdio Norte Ltda",
                "+5511988887777"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var namedDoc = JsonDocument.Parse(named);
        Assert.Equal("Estúdio Norte", namedDoc.RootElement.GetProperty("tenantTradeName").GetString());
        Assert.Equal("Estúdio Norte Ltda", namedDoc.RootElement.GetProperty("tenantLegalName").GetString());
        Assert.Equal("+5511988887777", namedDoc.RootElement.GetProperty("phone").GetString());
    }

    [Fact]
    public async Task OpenApi_schema_matches_the_committed_snapshot()
    {
        using var host = await StartSwaggerHostAsync();
        var client = host.GetTestClient();
        var response = await client.GetAsync("/swagger/v1/swagger.json");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);

        using var document = JsonDocument.Parse(body);
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        JsonElement? schema = null;
        foreach (var candidate in schemas.EnumerateObject())
        {
            if (candidate.Value.TryGetProperty("properties", out var properties)
                && properties.TryGetProperty("tenantTradeName", out _)
                && properties.TryGetProperty("tenantLegalName", out _)
                && properties.TryGetProperty("tenantId", out _))
            {
                schema = candidate.Value;
                break;
            }
        }

        Assert.True(schema.HasValue, body);
        Assert.True(
            document.RootElement.GetProperty("paths").TryGetProperty("/api/users/me", out var mePath),
            body);
        var responseSchema = mePath
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        Assert.Equal(
            "#/components/schemas/CurrentUserResponse",
            responseSchema.GetProperty("$ref").GetString());

        Assert.True(mePath.TryGetProperty("put", out var putPath), body);
        var putResponseSchema = putPath
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        Assert.Equal(
            "#/components/schemas/CurrentUserResponse",
            putResponseSchema.GetProperty("$ref").GetString());
        var requestSchema = putPath
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        AssertPhoneRequestSchema(document.RootElement, requestSchema);

        var actual = Canonicalize(schema!.Value);
        var snapshotPath = Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "current-user-response.openapi.txt");
        var expected = File.ReadAllText(snapshotPath).Trim();
        Assert.Equal(expected, actual);
    }

    private static void AssertPhoneRequestSchema(JsonElement document, JsonElement requestSchema)
    {
        JsonElement schema = requestSchema;
        if (requestSchema.TryGetProperty("$ref", out var reference))
        {
            var name = reference.GetString()!;
            const string prefix = "#/components/schemas/";
            Assert.StartsWith(prefix, name);
            schema = document
                .GetProperty("components")
                .GetProperty("schemas")
                .GetProperty(name[prefix.Length..]);
        }

        var phone = schema.GetProperty("properties").GetProperty("phone");
        Assert.Equal("string", phone.GetProperty("type").GetString());
        Assert.True(phone.TryGetProperty("nullable", out var nullable) && nullable.GetBoolean());
    }

    private static string Canonicalize(JsonElement schema)
    {
        var properties = schema.GetProperty("properties");
        var lines = new List<string>();
        foreach (var name in WireProperties)
        {
            Assert.True(properties.TryGetProperty(name, out var property), name);
            lines.Add($"{name}:{Describe(property)}");
        }

        var extras = properties.EnumerateObject()
            .Select(property => property.Name)
            .Except(WireProperties)
            .ToArray();
        Assert.Empty(extras);
        return string.Join('\n', lines);
    }

    private static string Describe(JsonElement property)
    {
        if (property.TryGetProperty("$ref", out var reference))
        {
            return reference.GetString() ?? "ref";
        }

        if (property.TryGetProperty("type", out var type))
        {
            var typeText = type.ValueKind == JsonValueKind.Array
                ? string.Join("|", type.EnumerateArray().Select(item => item.GetString()))
                : type.GetString();
            var nullable = property.TryGetProperty("nullable", out var nullableValue)
                && nullableValue.ValueKind == JsonValueKind.True;
            var format = property.TryGetProperty("format", out var formatValue)
                ? formatValue.GetString()
                : null;
            var items = property.TryGetProperty("items", out var itemsValue)
                ? Describe(itemsValue)
                : null;
            return string.Join(
                ";",
                new[]
                {
                    typeText,
                    nullable ? "nullable" : null,
                    format,
                    items is null ? null : $"items={items}",
                }.Where(part => !string.IsNullOrEmpty(part)));
        }

        if (property.TryGetProperty("allOf", out _)
            || property.TryGetProperty("anyOf", out _))
        {
            return property.GetRawText();
        }

        return property.GetRawText();
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

                            manager.ApplicationParts.Add(new AssemblyPart(typeof(UsersController).Assembly));
                            manager.FeatureProviders.Add(new UsersControllerOnly());
                        });
                    services.AddEndpointsApiExplorer();
                    services.AddSwaggerGen();
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

    private sealed class UsersControllerOnly : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) =>
            typeInfo.AsType() == typeof(UsersController);
    }
}
