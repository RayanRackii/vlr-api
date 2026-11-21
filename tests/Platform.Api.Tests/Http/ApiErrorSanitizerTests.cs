using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Api.Http;

namespace Platform.Api.Tests.Http;

public sealed class ApiErrorSanitizerTests
{
    [Theory]
    [InlineData(
        "JSON deserialization for type 'Platform.Api.Modules.Catalog.Dtos.CreateThing' was missing required properties, including the following: resourceQuantity",
        "Campo obrigatório ausente: resourceQuantity")]
    [InlineData(
        "JSON deserialization for type 'Platform.Api.Modules.Catalog.Dtos.CreateThing' was missing required properties including: 'name', 'unit'.",
        "Campo obrigatório ausente: name, unit")]
    [InlineData(
        "The JSON value could not be converted to System.Int32. Path: $.resourceQuantity | LineNumber: 0 | BytePositionInLine: 24.",
        "Valor inválido para o campo: resourceQuantity")]
    [InlineData("The Name field is required.", "The Name field is required.")]
    public void Sanitize_rewrites_framework_messages_and_keeps_plain_ones(string raw, string expected)
    {
        Assert.Equal(expected, ApiErrorSanitizer.Sanitize(raw));
    }

    [Fact]
    public void Sanitize_replaces_any_other_message_that_names_a_type()
    {
        var result = ApiErrorSanitizer.Sanitize("Cannot bind System.Collections.Generic.List`1[Platform.Api.X] here");

        Assert.Equal(ApiErrorSanitizer.GenericInvalidMessage, result);
    }

    [Fact]
    public async Task Missing_required_property_returns_400_without_type_names()
    {
        using var host = StartHost();
        var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync("/probe-sanitizer", new { other = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Platform.Api", body, StringComparison.Ordinal);
        Assert.DoesNotContain("JSON deserialization for type", body, StringComparison.Ordinal);
        Assert.Contains("resourceQuantity", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrong_value_type_returns_400_without_type_names()
    {
        using var host = StartHost();
        var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync("/probe-sanitizer", new { resourceQuantity = "abc" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("System.Int32", body, StringComparison.Ordinal);
        Assert.DoesNotContain("LineNumber", body, StringComparison.Ordinal);
    }

    private static IHost StartHost() => new HostBuilder().ConfigureWebHost(web =>
    {
        web.UseTestServer();
        web.ConfigureServices(services =>
        {
            services.AddControllers()
                .ConfigureApplicationPartManager(manager =>
                {
                    manager.ApplicationParts.Clear();
                    manager.ApplicationParts.Add(new AssemblyPart(typeof(SanitizerProbeController).Assembly));
                    foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToArray())
                    {
                        manager.FeatureProviders.Remove(provider);
                    }

                    manager.FeatureProviders.Add(new ProbeFeatureProvider());
                })
                .AddSanitizedModelStateErrors();
        });
        web.Configure(app =>
        {
            app.UseRouting();
            app.UseEndpoints(endpoints => endpoints.MapControllers());
        });
    }).Start();

    private sealed class ProbeFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature) =>
            feature.Controllers.Add(typeof(SanitizerProbeController).GetTypeInfo());
    }
}

public sealed class SanitizerProbeBody
{
    public required int ResourceQuantity { get; init; }
}

[ApiController]
[Route("probe-sanitizer")]
public sealed class SanitizerProbeController : ControllerBase
{
    [HttpPost]
    public IActionResult Post([FromBody] SanitizerProbeBody body) => Ok(body.ResourceQuantity);
}
