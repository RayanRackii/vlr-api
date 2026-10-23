using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Api.Modules.Users.Controllers;
using Platform.Api.Serialization;

namespace Platform.Api.Tests.Contracts;

public sealed class OpenApiFullSnapshotContractTests
{
    private const string SnapshotFileName = "openapi.full.snapshot.json";

    [Fact]
    public async Task Full_openapi_document_matches_the_committed_snapshot()
    {
        using var host = await StartSwaggerHostAsync();
        var response = await host.GetTestClient().GetAsync("/swagger/v1/swagger.json");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);

        using var document = JsonDocument.Parse(body);
        var actual = Normalize(document.RootElement);
        var sourceSnapshotPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "tests",
            "Platform.Api.Tests",
            "Contracts",
            SnapshotFileName);
        var outputSnapshotPath = Path.Combine(AppContext.BaseDirectory, "Contracts", SnapshotFileName);

        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI_SNAPSHOT") == "1")
        {
            var snapshotPath = File.Exists(sourceSnapshotPath) ? sourceSnapshotPath : outputSnapshotPath;
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            await File.WriteAllTextAsync(snapshotPath, actual + Environment.NewLine);
            return;
        }

        var readSnapshotPath = File.Exists(sourceSnapshotPath) ? sourceSnapshotPath : outputSnapshotPath;
        if (!File.Exists(readSnapshotPath))
        {
            Assert.Fail($"OpenAPI snapshot is missing. Set UPDATE_OPENAPI_SNAPSHOT=1 and rerun this test to create {sourceSnapshotPath}.");
        }

        var expected = Normalize(JsonDocument.Parse(await File.ReadAllTextAsync(readSnapshotPath)).RootElement);
        Assert.True(
            string.Equals(expected, actual, StringComparison.Ordinal),
            $"The full OpenAPI document changed. Review the API contract and run with UPDATE_OPENAPI_SNAPSHOT=1 to update {sourceSnapshotPath}.\n{CreateDiff(expected, actual)}");
    }

    private static string Normalize(JsonElement element)
    {
        var node = JsonNode.Parse(element.GetRawText())!;
        SortObjects(node);
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void SortObjects(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var sorted = obj.OrderBy(property => property.Key, StringComparer.Ordinal).ToArray();
            foreach (var property in sorted)
            {
                SortObjects(property.Value!);
            }

            var replacement = new JsonObject();
            foreach (var property in sorted)
            {
                replacement.Add(property.Key, property.Value?.DeepClone());
            }

            obj.Clear();
            foreach (var property in replacement)
            {
                obj.Add(property.Key, property.Value?.DeepClone());
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                {
                    SortObjects(item);
                }
            }
        }
    }

    private static string CreateDiff(string expected, string actual)
    {
        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');
        var firstDifference = Enumerable.Range(0, Math.Max(expectedLines.Length, actualLines.Length))
            .FirstOrDefault(index => index >= expectedLines.Length
                || index >= actualLines.Length
                || !string.Equals(expectedLines[index], actualLines[index], StringComparison.Ordinal));
        var expectedLine = firstDifference < expectedLines.Length ? expectedLines[firstDifference] : "<end>";
        var actualLine = firstDifference < actualLines.Length ? actualLines[firstDifference] : "<end>";
        return $"First difference at normalized line {firstDifference + 1}:\n- {expectedLine}\n+ {actualLine}";
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
                            manager.ApplicationParts.Add(new AssemblyPart(typeof(UsersController).Assembly));
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
}
