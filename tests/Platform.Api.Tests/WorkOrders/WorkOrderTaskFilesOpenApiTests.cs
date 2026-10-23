using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Api.Modules.WorkOrders.Controllers;
using Swashbuckle.AspNetCore;

namespace Platform.Api.Tests.WorkOrders;

public sealed class WorkOrderTaskFilesOpenApiTests
{
    [Fact]
    public async Task Swagger_document_contains_multipart_upload_operation()
    {
        using var host = await StartSwaggerHostAsync();
        var response = await host.GetTestClient().GetAsync("/swagger/v1/swagger.json");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, body);
        using var document = JsonDocument.Parse(body);
        var operation = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/work-orders/{id}/tasks/{taskId}/files")
            .GetProperty("post");
        Assert.Equal("multipart/form-data", operation
            .GetProperty("requestBody")
            .GetProperty("content")
            .EnumerateObject()
            .Select(content => content.Name)
            .Single());
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

                            manager.ApplicationParts.Add(new AssemblyPart(typeof(WorkOrderTaskFilesController).Assembly));
                            manager.FeatureProviders.Add(new WorkOrderTaskFilesControllerOnly());
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

    private sealed class WorkOrderTaskFilesControllerOnly : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) =>
            typeInfo.AsType() == typeof(WorkOrderTaskFilesController);
    }
}
