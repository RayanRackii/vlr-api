using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Modules.Assets.Services;
using Platform.Api.Modules.Users.Services;
using Platform.Api.Modules.WorkOrders;
using Platform.Api.Modules.WorkOrders.Controllers;
using Platform.Api.Modules.WorkOrders.Services;
using Platform.Api.Notifications;
using Platform.Api.Services.Trial;
using Platform.Api.Storage;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.WorkOrders;

public sealed class WorkOrderTaskImageTests
{
    [Fact]
    public async Task Upload_stores_a_tenant_scoped_object_and_sets_the_task_answer()
    {
        var scenario = await SeedAsync();
        using var scope = StartHost(scenario.DatabaseName);
        var jpeg = Jpeg(32);

        var uploaded = await PostFileAsync(
            scope.Host,
            scenario.Executor,
            scenario.TenantId,
            scenario.WorkOrderId,
            scenario.TaskId,
            jpeg,
            "image/jpeg",
            "court.jpg");
        var order = await SendAsync(
            scope.Host,
            HttpMethod.Get,
            $"/api/work-orders/{scenario.WorkOrderId}",
            scenario.Executor,
            scenario.TenantId);
        var url = await SendAsync(
            scope.Host,
            HttpMethod.Get,
            $"/api/work-orders/{scenario.WorkOrderId}/tasks/{scenario.TaskId}/files/{uploaded.Id}/url",
            scenario.Executor,
            scenario.TenantId);

        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        Assert.Equal("image/jpeg", uploaded.ContentType);
        Assert.Equal(jpeg.Length, uploaded.SizeBytes);
        Assert.Equal(uploaded.Id.ToString("D"), uploaded.Value);
        var stored = Assert.Single(scope.Storage.Uploads);
        Assert.Equal("catalog-private", stored.Bucket);
        Assert.StartsWith(
            $"{scenario.TenantId:N}/work-orders/{scenario.WorkOrderId:N}/tasks/{scenario.TaskId:N}/",
            stored.Key,
            StringComparison.Ordinal);
        Assert.Equal(jpeg, stored.Content);
        Assert.Equal(HttpStatusCode.OK, order.StatusCode);
        using (var orderDoc = JsonDocument.Parse(await order.Content.ReadAsStringAsync()))
        {
            var task = orderDoc.RootElement.GetProperty("tasks").EnumerateArray().Single();
            Assert.Equal(uploaded.Value, task.GetProperty("value").GetString());
        }

        Assert.Equal(HttpStatusCode.OK, url.StatusCode);
        using var urlDoc = JsonDocument.Parse(await url.Content.ReadAsStringAsync());
        Assert.False(urlDoc.RootElement.GetProperty("isPublic").GetBoolean());
        Assert.Contains(stored.Key, urlDoc.RootElement.GetProperty("url").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_after_upload_still_deletes_the_new_object()
    {
        var scenario = await SeedAsync();
        using var scope = StartHost(scenario.DatabaseName);
        using var cancellation = new CancellationTokenSource();
        scope.Storage.AfterUpload = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            UploadAsExecutorAsync(scope, scenario, cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Single(scope.Storage.Deletes);
        Assert.Empty(scope.Storage.Uploads);
        await using var verify = InMemoryAppDb.Create(
            new FakeTenantProvider { TenantId = scenario.TenantId },
            scenario.DatabaseName);
        Assert.Empty(await verify.WorkOrderTaskFiles.ToListAsync());
        Assert.Null((await verify.WorkOrderTasks.SingleAsync(task => task.Id == scenario.TaskId)).Value);
    }

    [Fact]
    public async Task Upload_failure_after_storing_the_object_attempts_to_delete_its_key()
    {
        var scenario = await SeedAsync();
        using var scope = StartHost(scenario.DatabaseName);
        var failure = new HttpRequestException("Upload acknowledgement lost.");
        scope.Storage.AfterUpload = () => throw failure;

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() =>
            UploadAsExecutorAsync(scope, scenario, CancellationToken.None));

        Assert.Same(failure, thrown);
        var deleted = Assert.Single(scope.Storage.Deletes);
        Assert.Equal("catalog-private", deleted.Bucket);
        Assert.True(WorkOrderImageRules.IsKeyForTask(
            deleted.Key, scenario.TenantId, scenario.WorkOrderId, scenario.TaskId));
        Assert.Empty(scope.Storage.Uploads);
        await using var verify = InMemoryAppDb.Create(
            new FakeTenantProvider { TenantId = scenario.TenantId },
            scenario.DatabaseName);
        Assert.Empty(await verify.WorkOrderTaskFiles.ToListAsync());
    }

    [Fact]
    public async Task Upload_rejects_a_content_type_that_is_not_an_image()
    {
        var scenario = await SeedAsync();
        using var scope = StartHost(scenario.DatabaseName);

        var response = await PostFileAsync(
            scope.Host,
            scenario.Executor,
            scenario.TenantId,
            scenario.WorkOrderId,
            scenario.TaskId,
            "%PDF-1.7"u8.ToArray(),
            "application/pdf",
            "notes.pdf");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(WorkOrderImageRules.TypeNotAllowed, response.ErrorCode);
        Assert.Empty(scope.Storage.Uploads);
    }

    [Fact]
    public async Task Upload_rejects_bytes_that_do_not_match_the_declared_type()
    {
        var scenario = await SeedAsync();
        using var scope = StartHost(scenario.DatabaseName);

        var response = await PostFileAsync(
            scope.Host,
            scenario.Executor,
            scenario.TenantId,
            scenario.WorkOrderId,
            scenario.TaskId,
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
            "image/jpeg",
            "court.jpg");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(WorkOrderImageRules.ContentMismatch, response.ErrorCode);
        Assert.Empty(scope.Storage.Uploads);
    }

    [Fact]
    public async Task Upload_rejects_an_image_over_five_megabytes()
    {
        var scenario = await SeedAsync();
        using var scope = StartHost(scenario.DatabaseName);

        var response = await PostFileAsync(
            scope.Host,
            scenario.Executor,
            scenario.TenantId,
            scenario.WorkOrderId,
            scenario.TaskId,
            Jpeg(checked((int)WorkOrderImageRules.MaxBytes + 1)),
            "image/jpeg",
            "huge.jpg");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(WorkOrderImageRules.TooLarge, response.ErrorCode);
        Assert.Empty(scope.Storage.Uploads);
    }

    [Fact]
    public async Task Upload_and_retrieval_for_another_tenant_are_not_found()
    {
        var scenario = await SeedAsync();
        using var scope = StartHost(scenario.DatabaseName);
        var fileId = Guid.NewGuid();

        var upload = await PostFileAsync(
            scope.Host,
            scenario.OtherExecutor,
            scenario.OtherTenantId,
            scenario.WorkOrderId,
            scenario.TaskId,
            Jpeg(16),
            "image/jpeg",
            "court.jpg");
        var url = await SendAsync(
            scope.Host,
            HttpMethod.Get,
            $"/api/work-orders/{scenario.WorkOrderId}/tasks/{scenario.TaskId}/files/{fileId}/url",
            scenario.OtherExecutor,
            scenario.OtherTenantId);

        Assert.Equal(HttpStatusCode.NotFound, upload.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, url.StatusCode);
        Assert.Empty(scope.Storage.Uploads);
    }

    [Fact]
    public async Task Upload_and_retrieval_without_execute_permission_are_forbidden()
    {
        var scenario = await SeedAsync();
        using var scope = StartHost(scenario.DatabaseName);

        var upload = await PostFileAsync(
            scope.Host,
            scenario.Reader,
            scenario.TenantId,
            scenario.WorkOrderId,
            scenario.TaskId,
            Jpeg(16),
            "image/jpeg",
            "court.jpg");
        var url = await SendAsync(
            scope.Host,
            HttpMethod.Get,
            $"/api/work-orders/{scenario.WorkOrderId}/tasks/{scenario.TaskId}/files/{Guid.NewGuid()}/url",
            scenario.Reader,
            scenario.TenantId);

        Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);
        Assert.Equal(RbacErrorCodes.Forbidden, upload.Error);
        Assert.Equal(HttpStatusCode.Forbidden, url.StatusCode);
        Assert.Equal(RbacErrorCodes.Forbidden, await ReadErrorAsync(url));
        Assert.Empty(scope.Storage.Uploads);
    }

    [Fact]
    public async Task Upload_for_an_order_the_caller_cannot_access_is_not_found()
    {
        var scenario = await SeedAsync();
        using var scope = StartHost(scenario.DatabaseName);

        var upload = await PostFileAsync(
            scope.Host,
            scenario.Stranger,
            scenario.TenantId,
            scenario.WorkOrderId,
            scenario.TaskId,
            Jpeg(16),
            "image/jpeg",
            "court.jpg");

        Assert.Equal(HttpStatusCode.NotFound, upload.StatusCode);
        Assert.Empty(scope.Storage.Uploads);
    }

    [Fact]
    public async Task Required_photo_blocks_completion_until_the_stored_reference_is_the_answer()
    {
        var scenario = await SeedAsync();
        using var scope = StartHost(scenario.DatabaseName);

        var blocked = await SendAsync(
            scope.Host,
            HttpMethod.Patch,
            $"/api/work-orders/{scenario.WorkOrderId}/status",
            scenario.Executor,
            scenario.TenantId,
            """{"status":"Completed"}""");
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        using (var blockedDoc = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync()))
        {
            Assert.Equal("MANDATORY_TASKS_INCOMPLETE", blockedDoc.RootElement.GetProperty("code").GetString());
            var task = Assert.Single(blockedDoc.RootElement.GetProperty("tasks").EnumerateArray());
            Assert.Equal(scenario.TaskId, task.GetProperty("id").GetGuid());
        }

        var uploaded = await PostFileAsync(
            scope.Host,
            scenario.Executor,
            scenario.TenantId,
            scenario.WorkOrderId,
            scenario.TaskId,
            Jpeg(24),
            "image/jpeg",
            "court.jpg");
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);

        var forged = await SendAsync(
            scope.Host,
            HttpMethod.Patch,
            $"/api/work-orders/{scenario.WorkOrderId}/tasks/{scenario.TaskId}",
            scenario.Executor,
            scenario.TenantId,
            $$"""{"value":"{{Guid.NewGuid():D}}"}""");
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        using (var forgedDoc = JsonDocument.Parse(await forged.Content.ReadAsStringAsync()))
        {
            Assert.Equal("INVALID_TASK_VALUE", forgedDoc.RootElement.GetProperty("code").GetString());
            Assert.Equal(
                InvalidTaskValueException.InvalidImageReference,
                forgedDoc.RootElement.GetProperty("reason").GetString());
        }

        var accepted = await SendAsync(
            scope.Host,
            HttpMethod.Patch,
            $"/api/work-orders/{scenario.WorkOrderId}/tasks/{scenario.TaskId}",
            scenario.Executor,
            scenario.TenantId,
            $$"""{"value":"{{uploaded.Value}}"}""");
        var completed = await SendAsync(
            scope.Host,
            HttpMethod.Patch,
            $"/api/work-orders/{scenario.WorkOrderId}/status",
            scenario.Executor,
            scenario.TenantId,
            """{"status":"Completed"}""");

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        using var completedDoc = JsonDocument.Parse(await completed.Content.ReadAsStringAsync());
        Assert.Equal("Completed", completedDoc.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            uploaded.Value,
            completedDoc.RootElement.GetProperty("tasks").EnumerateArray().Single().GetProperty("value").GetString());
    }

    [Fact]
    public async Task Upload_on_a_closed_order_is_conflict_and_deletes_only_the_new_object()
    {
        var scenario = await SeedAsync();
        using var scope = StartHost(scenario.DatabaseName);

        var uploaded = await PostFileAsync(
            scope.Host,
            scenario.Executor,
            scenario.TenantId,
            scenario.WorkOrderId,
            scenario.TaskId,
            Jpeg(24),
            "image/jpeg",
            "court.jpg");
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var kept = Assert.Single(scope.Storage.Uploads);

        var completed = await SendAsync(
            scope.Host,
            HttpMethod.Patch,
            $"/api/work-orders/{scenario.WorkOrderId}/status",
            scenario.Executor,
            scenario.TenantId,
            """{"status":"Completed"}""");
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);

        var rejected = await PostFileAsync(
            scope.Host,
            scenario.Executor,
            scenario.TenantId,
            scenario.WorkOrderId,
            scenario.TaskId,
            Jpeg(40),
            "image/jpeg",
            "later.jpg");

        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal(WorkOrderErrorCodes.Closed, rejected.ErrorCode);
        var remaining = Assert.Single(scope.Storage.Uploads);
        Assert.Equal(kept.Key, remaining.Key);
        var deleted = Assert.Single(scope.Storage.Deletes);
        Assert.NotEqual(kept.Key, deleted.Key);
        Assert.StartsWith(
            $"{scenario.TenantId:N}/work-orders/{scenario.WorkOrderId:N}/tasks/{scenario.TaskId:N}/",
            deleted.Key,
            StringComparison.Ordinal);

        var order = await SendAsync(
            scope.Host,
            HttpMethod.Get,
            $"/api/work-orders/{scenario.WorkOrderId}",
            scenario.Executor,
            scenario.TenantId);
        Assert.Equal(HttpStatusCode.OK, order.StatusCode);
        using (var orderDoc = JsonDocument.Parse(await order.Content.ReadAsStringAsync()))
        {
            Assert.Equal(
                uploaded.Value,
                orderDoc.RootElement.GetProperty("tasks").EnumerateArray().Single().GetProperty("value").GetString());
        }

        var url = await SendAsync(
            scope.Host,
            HttpMethod.Get,
            $"/api/work-orders/{scenario.WorkOrderId}/tasks/{scenario.TaskId}/files/{uploaded.Id}/url",
            scenario.Executor,
            scenario.TenantId);
        Assert.Equal(HttpStatusCode.OK, url.StatusCode);
        using var urlDoc = JsonDocument.Parse(await url.Content.ReadAsStringAsync());
        Assert.Contains(kept.Key, urlDoc.RootElement.GetProperty("url").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clearing_the_photo_answer_keeps_the_stored_object()
    {
        var scenario = await SeedAsync();
        using var scope = StartHost(scenario.DatabaseName);
        var uploaded = await PostFileAsync(
            scope.Host,
            scenario.Executor,
            scenario.TenantId,
            scenario.WorkOrderId,
            scenario.TaskId,
            Jpeg(24),
            "image/jpeg",
            "court.jpg");
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);

        var cleared = await SendAsync(
            scope.Host,
            HttpMethod.Patch,
            $"/api/work-orders/{scenario.WorkOrderId}/tasks/{scenario.TaskId}",
            scenario.Executor,
            scenario.TenantId,
            """{"value":null}""");
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        var order = await SendAsync(
            scope.Host,
            HttpMethod.Get,
            $"/api/work-orders/{scenario.WorkOrderId}",
            scenario.Executor,
            scenario.TenantId);
        Assert.Equal(HttpStatusCode.OK, order.StatusCode);
        using (var orderDoc = JsonDocument.Parse(await order.Content.ReadAsStringAsync()))
        {
            Assert.Equal(
                JsonValueKind.Null,
                orderDoc.RootElement.GetProperty("tasks").EnumerateArray().Single().GetProperty("value").ValueKind);
        }

        var blocked = await SendAsync(
            scope.Host,
            HttpMethod.Patch,
            $"/api/work-orders/{scenario.WorkOrderId}/status",
            scenario.Executor,
            scenario.TenantId,
            """{"status":"Completed"}""");
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        using (var blockedDoc = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync()))
        {
            Assert.Equal(WorkOrderErrorCodes.MandatoryTasksIncomplete, blockedDoc.RootElement.GetProperty("code").GetString());
        }

        var url = await SendAsync(
            scope.Host,
            HttpMethod.Get,
            $"/api/work-orders/{scenario.WorkOrderId}/tasks/{scenario.TaskId}/files/{uploaded.Id}/url",
            scenario.Executor,
            scenario.TenantId);
        Assert.Equal(HttpStatusCode.OK, url.StatusCode);
        Assert.Empty(scope.Storage.Deletes);
        Assert.Single(scope.Storage.Uploads);
    }

    [Fact]
    public async Task Patch_rejects_a_file_id_that_belongs_to_another_task()
    {
        var scenario = await SeedAsync(secondImageTask: true);
        Assert.NotNull(scenario.SecondTaskId);
        using var scope = StartHost(scenario.DatabaseName);
        var uploaded = await PostFileAsync(
            scope.Host,
            scenario.Executor,
            scenario.TenantId,
            scenario.WorkOrderId,
            scenario.TaskId,
            Jpeg(24),
            "image/jpeg",
            "court.jpg");
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);

        var rejected = await SendAsync(
            scope.Host,
            HttpMethod.Patch,
            $"/api/work-orders/{scenario.WorkOrderId}/tasks/{scenario.SecondTaskId}",
            scenario.Executor,
            scenario.TenantId,
            $$"""{"value":"{{uploaded.Value}}"}""");

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var rejectedDoc = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Assert.Equal(WorkOrderErrorCodes.InvalidTaskValue, rejectedDoc.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            InvalidTaskValueException.InvalidImageReference,
            rejectedDoc.RootElement.GetProperty("reason").GetString());

        var order = await SendAsync(
            scope.Host,
            HttpMethod.Get,
            $"/api/work-orders/{scenario.WorkOrderId}",
            scenario.Executor,
            scenario.TenantId);
        Assert.Equal(HttpStatusCode.OK, order.StatusCode);
        using var orderDoc = JsonDocument.Parse(await order.Content.ReadAsStringAsync());
        var tasks = orderDoc.RootElement.GetProperty("tasks").EnumerateArray().ToList();
        var first = tasks.Single(task => task.GetProperty("id").GetGuid() == scenario.TaskId);
        var second = tasks.Single(task => task.GetProperty("id").GetGuid() == scenario.SecondTaskId);
        Assert.Equal(uploaded.Value, first.GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task Patch_limits_text_task_values_to_1000_characters_after_trimming()
    {
        var scenario = await SeedAsync(taskInputType: TaskInputType.Text, taskConfiguration: "{\"maxLength\":8000}");
        using var scope = StartHost(scenario.DatabaseName);

        var atLimit = await SendAsync(
            scope.Host,
            HttpMethod.Patch,
            $"/api/work-orders/{scenario.WorkOrderId}/tasks/{scenario.TaskId}",
            scenario.Executor,
            scenario.TenantId,
            JsonSerializer.Serialize(new { value = new string('a', 1000) }));
        Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);

        var tooLong = await SendAsync(
            scope.Host,
            HttpMethod.Patch,
            $"/api/work-orders/{scenario.WorkOrderId}/tasks/{scenario.TaskId}",
            scenario.Executor,
            scenario.TenantId,
            JsonSerializer.Serialize(new { value = new string('a', 1001) }));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        using (var error = JsonDocument.Parse(await tooLong.Content.ReadAsStringAsync()))
        {
            Assert.Equal(WorkOrderErrorCodes.TaskNoteTooLong, error.RootElement.GetProperty("code").GetString());
            Assert.Contains("1000", error.RootElement.GetProperty("error").GetString());
        }

        var trailingSpace = await SendAsync(
            scope.Host,
            HttpMethod.Patch,
            $"/api/work-orders/{scenario.WorkOrderId}/tasks/{scenario.TaskId}",
            scenario.Executor,
            scenario.TenantId,
            JsonSerializer.Serialize(new { value = new string('a', 1000) + " " }));
        Assert.Equal(HttpStatusCode.OK, trailingSpace.StatusCode);
        using var response = JsonDocument.Parse(await trailingSpace.Content.ReadAsStringAsync());
        Assert.Equal(1000, response.RootElement.GetProperty("tasks")[0].GetProperty("value").GetString()?.Length);
    }

    [Fact]
    public async Task Upload_and_retrieval_are_forbidden_when_the_os_module_is_inactive()
    {
        var scenario = await SeedAsync(moduleActive: false);
        using var scope = StartHost(scenario.DatabaseName, allowlistedExecute: true);

        var upload = await PostFileAsync(
            scope.Host,
            scenario.Executor,
            scenario.TenantId,
            scenario.WorkOrderId,
            scenario.TaskId,
            Jpeg(16),
            "image/jpeg",
            "court.jpg");
        var url = await SendAsync(
            scope.Host,
            HttpMethod.Get,
            $"/api/work-orders/{scenario.WorkOrderId}/tasks/{scenario.TaskId}/files/{Guid.NewGuid()}/url",
            scenario.Executor,
            scenario.TenantId);

        Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);
        Assert.Equal(RequireActiveModuleAttribute.InactiveModuleError, upload.Error);
        Assert.Equal(HttpStatusCode.Forbidden, url.StatusCode);
        Assert.Equal(RequireActiveModuleAttribute.InactiveModuleError, await ReadErrorAsync(url));
        Assert.Empty(scope.Storage.Uploads);
    }

    [Fact]
    public async Task Replace_keeps_the_file_id_and_deletes_the_previous_task_object()
    {
        var scenario = await SeedAsync();
        using var scope = StartHost(scenario.DatabaseName);

        var first = await PostFileAsync(
            scope.Host,
            scenario.Executor,
            scenario.TenantId,
            scenario.WorkOrderId,
            scenario.TaskId,
            Jpeg(24),
            "image/jpeg",
            "court.jpg");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var previous = Assert.Single(scope.Storage.Uploads);

        var second = await PostFileAsync(
            scope.Host,
            scenario.Executor,
            scenario.TenantId,
            scenario.WorkOrderId,
            scenario.TaskId,
            Jpeg(32),
            "image/jpeg",
            "court-2.jpg");

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(second.Id.ToString("D"), second.Value);
        var kept = Assert.Single(scope.Storage.Uploads);
        Assert.NotEqual(previous.Key, kept.Key);
        var deleted = Assert.Single(scope.Storage.Deletes);
        Assert.Equal(previous.Key, deleted.Key);
        Assert.Equal("catalog-private", deleted.Bucket);
    }

    [Fact]
    public async Task Replace_does_not_delete_a_storage_key_outside_the_task()
    {
        var scenario = await SeedAsync();
        var fileId = await AttachForeignKeyAsync(scenario);
        using var scope = StartHost(scenario.DatabaseName);

        var uploaded = await PostFileAsync(
            scope.Host,
            scenario.Executor,
            scenario.TenantId,
            scenario.WorkOrderId,
            scenario.TaskId,
            Jpeg(24),
            "image/jpeg",
            "court.jpg");

        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        Assert.Equal(fileId, uploaded.Id);
        Assert.Empty(scope.Storage.Deletes);
        var stored = Assert.Single(scope.Storage.Uploads);
        Assert.True(WorkOrderImageRules.IsKeyForTask(
            stored.Key,
            scenario.TenantId,
            scenario.WorkOrderId,
            scenario.TaskId));
        Assert.False(WorkOrderImageRules.IsKeyForTask(
            "catalog/other",
            scenario.TenantId,
            scenario.WorkOrderId,
            scenario.TaskId));
    }

    private static async Task UploadAsExecutorAsync(
        HostScope scope,
        Scenario scenario,
        CancellationToken cancellationToken)
    {
        using var services = scope.Host.Services.CreateScope();
        var accessor = services.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var previousContext = accessor.HttpContext;
        accessor.HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("sub", scenario.Executor),
                    new Claim("app_metadata", $$"""{"tenant_id":"{{scenario.TenantId}}"}"""),
                ],
                "Test")),
        };
        try
        {
            await using var content = new MemoryStream(Jpeg(24));
            await services.ServiceProvider.GetRequiredService<IWorkOrderTaskImageService>()
                .UploadAsync(scenario.WorkOrderId, scenario.TaskId, "court.jpg", "image/jpeg", content, cancellationToken);
        }
        finally
        {
            accessor.HttpContext = previousContext;
        }
    }

    private static byte[] Jpeg(int size)
    {
        var bytes = new byte[size];
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        return bytes;
    }

    private static async Task<UploadResult> PostFileAsync(
        IHost host,
        string user,
        Guid tenantId,
        Guid workOrderId,
        Guid taskId,
        byte[] content,
        string mediaType,
        string fileName)
    {
        using var body = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        body.Add(file, "file", fileName);
        var response = await SendAsync(
            host,
            HttpMethod.Post,
            $"/api/work-orders/{workOrderId}/tasks/{taskId}/files",
            user,
            tenantId,
            body);
        return await UploadResult.ReadAsync(response);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        IHost host,
        HttpMethod method,
        string path,
        string user,
        Guid tenantId,
        HttpContent? content = null)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, tenantId.ToString());
        var request = new HttpRequestMessage(method, path) { Content = content };
        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendAsync(
        IHost host,
        HttpMethod method,
        string path,
        string user,
        Guid tenantId,
        string json)
    {
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        return SendAsync(host, method, path, user, tenantId, content);
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("error").GetString();
    }

    private static async Task<Scenario> SeedAsync(
        bool secondImageTask = false,
        bool moduleActive = true,
        TaskInputType taskInputType = TaskInputType.Image,
        string? taskConfiguration = null)
    {
        var databaseName = $"wo-image-{Guid.NewGuid():N}";
        var tenant = new Tenant("Image Club", "44444444000191", subdomain: "image-club");
        var other = new Tenant("Other Club", "55555555000199", subdomain: "other-club");
        var provider = new FakeTenantProvider { TenantId = tenant.Id };
        await using var db = InMemoryAppDb.Create(provider, databaseName);
        db.Tenants.AddRange(tenant, other);
        db.TenantModules.Add(new TenantModule(tenant.Id, PlatformModules.WorkOrders, moduleActive));
        db.TenantModules.Add(new TenantModule(other.Id, PlatformModules.WorkOrders, isActive: true));

        var execute = new Permission(
            Permissions.Os.WorkOrdersExecute,
            "Execute work orders",
            moduleKey: PlatformModules.WorkOrders);
        var read = new Permission(
            Permissions.Os.WorkOrdersRead,
            "Read work orders",
            moduleKey: PlatformModules.WorkOrders);
        db.Permissions.AddRange(execute, read);

        var executorRole = new Role(tenant.Id, "Executor");
        var readerRole = new Role(tenant.Id, "Reader");
        var otherRole = new Role(other.Id, "Executor");
        db.Roles.AddRange(executorRole, readerRole, otherRole);
        db.RolePermissions.Add(new RolePermission(executorRole.Id, execute.Id));
        db.RolePermissions.Add(new RolePermission(executorRole.Id, read.Id));
        db.RolePermissions.Add(new RolePermission(readerRole.Id, read.Id));
        db.RolePermissions.Add(new RolePermission(otherRole.Id, execute.Id));
        db.RolePermissions.Add(new RolePermission(otherRole.Id, read.Id));

        var unit = new Unit(tenant.Id, "Matriz");
        var family = new AssetFamily
        {
            Key = $"spaces-{Guid.NewGuid():N}"[..20],
            Label = "Spaces",
            FieldSchemaJson = """{"fields":[]}""",
        };
        var category = new AssetCategory { TenantId = tenant.Id, Name = "Quadras" };
        db.Units.Add(unit);
        db.AssetFamilies.Add(family);
        db.AssetCategories.Add(category);
        await db.SaveChangesAsync();

        var asset = new Asset
        {
            TenantId = tenant.Id,
            UnitId = unit.Id,
            CategoryId = category.Id,
            FamilyId = family.Id,
            Name = "Quadra 1",
            Tag = "Q1",
            Status = AssetStatus.Active,
        };
        db.Assets.Add(asset);

        const string executor = "executor-a";
        const string reader = "reader-a";
        const string stranger = "stranger-a";
        const string otherExecutor = "executor-b";
        var executorUser = new User(tenant.Id, executor, "Executor", "exec@image.test");
        var readerUser = new User(tenant.Id, reader, "Reader", "read@image.test");
        var strangerUser = new User(tenant.Id, stranger, "Stranger", "stranger@image.test");
        var otherUser = new User(other.Id, otherExecutor, "Other", "exec@other.test");
        db.Users.AddRange(executorUser, readerUser, strangerUser, otherUser);
        db.UserRoles.Add(new UserRole(executorUser.Id, executorRole.Id));
        db.UserRoles.Add(new UserRole(readerUser.Id, readerRole.Id));
        db.UserRoles.Add(new UserRole(strangerUser.Id, executorRole.Id));
        db.UserRoles.Add(new UserRole(otherUser.Id, otherRole.Id));

        var order = new WorkOrder
        {
            TenantId = tenant.Id,
            AssetId = asset.Id,
            AssignedUserId = executorUser.Id,
            Status = WorkOrderStatus.Pending,
            ScheduledDate = new DateOnly(2026, 10, 18),
        };
        var task = new WorkOrderTask
        {
            TenantId = tenant.Id,
            WorkOrderId = order.Id,
            Title = "Foto da quadra",
            InputType = taskInputType,
            Configuration = taskConfiguration,
            IsMandatory = true,
            Order = 1,
        };
        order.AddTask(task);
        Guid? secondTaskId = null;
        if (secondImageTask)
        {
            var second = new WorkOrderTask
            {
                TenantId = tenant.Id,
                WorkOrderId = order.Id,
                Title = "Foto do vestiário",
                InputType = TaskInputType.Image,
                IsMandatory = true,
                Order = 2,
            };
            order.AddTask(second);
            secondTaskId = second.Id;
        }

        db.WorkOrders.Add(order);
        await db.SaveChangesAsync();

        return new Scenario(
            databaseName,
            tenant.Id,
            other.Id,
            order.Id,
            task.Id,
            executor,
            reader,
            stranger,
            otherExecutor,
            secondTaskId);
    }

    private static async Task<Guid> AttachForeignKeyAsync(Scenario scenario)
    {
        var provider = new FakeTenantProvider { TenantId = scenario.TenantId };
        await using var db = InMemoryAppDb.Create(provider, scenario.DatabaseName);
        var file = new WorkOrderTaskFile
        {
            TenantId = scenario.TenantId,
            WorkOrderId = scenario.WorkOrderId,
            WorkOrderTaskId = scenario.TaskId,
            StorageKey = "catalog/other",
            FileName = "other.jpg",
            ContentType = "image/jpeg",
            SizeBytes = 3,
        };
        db.WorkOrderTaskFiles.Add(file);
        var task = await db.WorkOrderTasks.SingleAsync(item => item.Id == scenario.TaskId);
        task.Value = file.Id.ToString("D");
        await db.SaveChangesAsync();
        return file.Id;
    }

    private static HostScope StartHost(string databaseName, bool allowlistedExecute = false)
    {
        var storage = new RecordingStorageProvider();
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddHttpContextAccessor();
                    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
                    services.AddSingleton<IHostEnvironment, FakeHostEnvironment>();
                    services.AddSingleton<IPlatformAdminChecker>(_ => new FakePlatformAdminChecker());
                    services.AddScoped<AmbientTenantContext>();
                    services.AddScoped<PlatformAdminTenantSession>();
                    services.AddScoped<ITenantProvider, HttpContextTenantProvider>();
                    services.AddScoped<AppDbContext>(sp =>
                        InMemoryAppDb.Create(sp.GetRequiredService<ITenantProvider>(), databaseName));
                    services.AddScoped<ITenantModuleAccessor, TenantModuleAccessor>();
                    services.AddScoped<IPermissionResolver, PermissionResolver>();
                    if (allowlistedExecute)
                    {
                        services.AddSingleton<IAuthorizationHandler>(
                            new AllowlistedPermissionHandler([Permissions.Os.WorkOrdersExecute]));
                    }
                    else
                    {
                        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
                    }
                    services.AddScoped<IRbacGrantGuard>(sp => new RbacGrantGuard(
                        sp.GetRequiredService<AppDbContext>(),
                        sp.GetRequiredService<IPermissionResolver>(),
                        NullLogger<RbacGrantGuard>.Instance));
                    services.AddSingleton<ITrialGuard, FakeTrialGuard>();
                    services.AddSingleton<NotificationQueue>();
                    services.AddScoped<IUserDirectoryService, UserDirectoryService>();
                    services.AddScoped<IAssetService, AssetService>();
                    services.AddScoped<IAssetFamilyService, AssetFamilyService>();
                    services.AddScoped<IAssetCategoryService, AssetCategoryService>();
                    services.AddScoped<IAssetRegistry, AssetRegistry>();
                    services.AddScoped<IWorkOrderService, WorkOrderService>();
                    services.AddScoped<IWorkOrderGenerationService, WorkOrderGenerationService>();
                    services.AddSingleton<IOptions<StorageOptions>>(Options.Create(new StorageOptions()));
                    services.AddSingleton<RecordingStorageProvider>(storage);
                    services.AddSingleton<IStorageProvider>(storage);
                    services.AddScoped<IWorkOrderTaskImageService, WorkOrderTaskImageService>();
                    services.AddAuthorization(options => options.AddRolvixPolicies());
                    services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
                    services.AddSingleton<IAuthorizationMiddlewareResultHandler, JsonForbiddenAuthorizationResultHandler>();
                    services
                        .AddAuthentication(SupabaseJwtBearerDefaults.AuthenticationScheme)
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                            SupabaseJwtBearerDefaults.AuthenticationScheme,
                            _ => { });
                    services.AddControllers()
                        .AddJsonOptions(options =>
                        {
                            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                        })
                        .ConfigureApplicationPartManager(manager =>
                        {
                            manager.ApplicationParts.Clear();
                            foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
                            {
                                manager.FeatureProviders.Remove(provider);
                            }

                            manager.ApplicationParts.Add(new AssemblyPart(typeof(WorkOrdersController).Assembly));
                            manager.FeatureProviders.Add(new SelectedControllers(
                                typeof(WorkOrdersController),
                                typeof(WorkOrderTaskFilesController)));
                        });
                });
                web.Configure(app =>
                {
                    app.UseExceptionHandler(errorApp =>
                    {
                        errorApp.Run(async context =>
                        {
                            var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
                            context.Response.StatusCode = exception is UnauthorizedAccessException or TenantResolutionException
                                ? StatusCodes.Status401Unauthorized
                                : StatusCodes.Status500InternalServerError;
                            await context.Response.WriteAsJsonAsync(new
                            {
                                error = exception?.Message ?? "An unexpected error occurred.",
                            });
                        });
                    });
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .Start();
        return new HostScope(host, storage);
    }

    private sealed record Scenario(
        string DatabaseName,
        Guid TenantId,
        Guid OtherTenantId,
        Guid WorkOrderId,
        Guid TaskId,
        string Executor,
        string Reader,
        string Stranger,
        string OtherExecutor,
        Guid? SecondTaskId = null);

    private sealed class HostScope(IHost host, RecordingStorageProvider storage) : IDisposable
    {
        public IHost Host { get; } = host;

        public RecordingStorageProvider Storage { get; } = storage;

        public void Dispose() => Host.Dispose();
    }

    private sealed record UploadResult(
        HttpStatusCode StatusCode,
        string? Error,
        string? ErrorCode,
        Guid Id,
        string? ContentType,
        long SizeBytes,
        string? Value)
    {
        public static async Task<UploadResult> ReadAsync(HttpResponseMessage response)
        {
            var body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != HttpStatusCode.Created)
            {
                string? error = null;
                string? code = null;
                if (!string.IsNullOrWhiteSpace(body))
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("error", out var errorProperty))
                    {
                        error = errorProperty.GetString();
                    }

                    if (doc.RootElement.TryGetProperty("code", out var codeProperty))
                    {
                        code = codeProperty.GetString();
                    }
                }

                return new UploadResult(response.StatusCode, error, code, Guid.Empty, null, 0, null);
            }

            using var created = JsonDocument.Parse(body);
            var root = created.RootElement;
            return new UploadResult(
                response.StatusCode,
                null,
                null,
                root.GetProperty("id").GetGuid(),
                root.GetProperty("contentType").GetString(),
                root.GetProperty("sizeBytes").GetInt64(),
                root.GetProperty("value").GetString());
        }
    }

    private sealed class RecordingStorageProvider : IStorageProvider
    {
        public Action? AfterUpload { get; set; }

        public List<StoredObject> Uploads { get; } = [];

        public List<DeletedObject> Deletes { get; } = [];

        public Task UploadAsync(
            string bucket,
            string key,
            Stream stream,
            string contentType,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            Uploads.Add(new StoredObject(bucket, key, buffer.ToArray(), contentType));
            AfterUpload?.Invoke();
            return Task.CompletedTask;
        }

        public string GetPublicUrl(string bucket, string key) =>
            throw new InvalidOperationException("Public URLs are only available for the public bucket.");

        public Task<string> CreateSignedUrlAsync(
            string bucket,
            string key,
            TimeSpan ttl,
            CancellationToken cancellationToken = default) =>
            Task.FromResult($"https://storage.test/{bucket}/{key}?exp={(int)ttl.TotalSeconds}");

        public Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Deletes.Add(new DeletedObject(bucket, key));
            Uploads.RemoveAll(item => item.Bucket == bucket && item.Key == key);
            return Task.CompletedTask;
        }
    }

    private sealed record StoredObject(string Bucket, string Key, byte[] Content, string ContentType);

    private sealed record DeletedObject(string Bucket, string Key);

    private sealed class AllowlistedPermissionHandler(IReadOnlyCollection<string> allowedKeys)
        : AuthorizationHandler<PermissionRequirement>
    {
        protected override Task HandleRequirementAsync(
            AuthorizationHandlerContext context,
            PermissionRequirement requirement)
        {
            if (allowedKeys.Contains(requirement.PermissionKey))
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class SelectedControllers(params Type[] controllerTypes) : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) =>
            controllerTypes.Contains(typeInfo.AsType());
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string UserHeader = "X-Test-User";

        public const string TenantHeader = "X-Test-Tenant";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out var userValues)
                || string.IsNullOrWhiteSpace(userValues.ToString()))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var user = userValues.ToString();
            var claims = new List<Claim>
            {
                new("email", user),
                new(ClaimTypes.Email, user),
                new(ClaimTypes.Name, user),
                new("sub", user),
            };

            if (Request.Headers.TryGetValue(TenantHeader, out var tenantValues)
                && Guid.TryParse(tenantValues.ToString(), out var tenantId))
            {
                claims.Add(new Claim(
                    TenantClaimTypes.AppMetadata,
                    $"{{\"tenant_id\":\"{tenantId}\"}}"));
            }

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
