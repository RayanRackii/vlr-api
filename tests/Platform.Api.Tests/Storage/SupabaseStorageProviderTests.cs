using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Api.Storage;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Tests.Storage;

public sealed class SupabaseStorageProviderTests
{
    private const string SecretKey = "sb_secret_dummy";
    private const string PublishableKey = "sb_publishable_dummy";
    private const string DummyJwt = "eyJhbGciOiJub25lIn0.eyJzdWIiOiJkdW1teSJ9.dummy-signature-not-a-secret";
    private const string Bucket = "catalog-private";
    private const string ObjectKey = "tenants/t1/products/p1/file.bin";

    [Fact]
    public async Task Upload_secret_key_sends_apikey_without_authorization()
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, "{}"));
        var sut = CreateSut(handler, SecretKey);

        await sut.UploadAsync(Bucket, ObjectKey, new MemoryStream([1, 2, 3]), "text/plain");

        Assert.NotNull(handler.Request);
        Assert.Equal(SecretKey, GetApiKey(handler.Request!));
        Assert.Null(handler.Request!.Headers.Authorization);
    }

    [Fact]
    public async Task Upload_jwt_sends_apikey_and_bearer()
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, "{}"));
        var sut = CreateSut(handler, DummyJwt);

        await sut.UploadAsync(Bucket, ObjectKey, new MemoryStream([1]), "text/plain");

        Assert.NotNull(handler.Request);
        Assert.Equal(DummyJwt, GetApiKey(handler.Request!));
        Assert.Equal("Bearer", handler.Request!.Headers.Authorization?.Scheme);
        Assert.Equal(DummyJwt, handler.Request!.Headers.Authorization?.Parameter);
    }

    [Fact]
    public void Constructor_publishable_key_throws_without_leaking_credential()
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, "{}"));

        var ex = Assert.Throws<InvalidOperationException>(() => CreateSut(handler, PublishableKey));

        Assert.DoesNotContain(PublishableKey, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(PublishableKey, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_200_succeeds()
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, "{}"));
        var sut = CreateSut(handler, SecretKey);

        await sut.UploadAsync(Bucket, ObjectKey, new MemoryStream([1]), "text/plain");
    }

    [Fact]
    public async Task DeletePrefix_traverses_relative_entries_and_removes_only_full_keys_under_the_prefix()
    {
        const string prefix = "tenant-a/";
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return body.Contains("\"prefix\":\"tenant-a/nested/\"", StringComparison.Ordinal)
                    ? Json(HttpStatusCode.OK, """[{"name":"inside.bin","id":"file-2"}]""")
                    : Json(HttpStatusCode.OK, """[{"name":"file.bin","id":"file-1"},{"name":"nested","id":null}]""");
            }

            return Json(HttpStatusCode.OK, "[]");
        });
        var sut = CreateSut(handler, SecretKey);

        await sut.DeletePrefixAsync(Bucket, prefix);

        var listRequests = handler.Requests.Where(request => request.Method == HttpMethod.Post).ToArray();
        Assert.Collection(
            listRequests,
            request => Assert.Contains($"\"prefix\":\"{prefix}\"", request.Body, StringComparison.Ordinal),
            request => Assert.Contains("\"prefix\":\"tenant-a/nested/\"", request.Body, StringComparison.Ordinal));

        var deleteRequest = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Delete);
        Assert.Equal($"/storage/v1/object/{Bucket}", deleteRequest.Path);
        Assert.Contains("tenant-a/file.bin", deleteRequest.Body, StringComparison.Ordinal);
        Assert.Contains("tenant-a/nested/inside.bin", deleteRequest.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"file.bin\"", deleteRequest.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"inside.bin\"", deleteRequest.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletePrefix_advances_the_listing_offset_before_deleting_a_full_page()
    {
        const string prefix = "tenant-a/";
        var listCalls = 0;
        var fullPage = $"[{string.Join(',', Enumerable.Range(0, 1000)
            .Select(index => $"{{\"name\":\"file-{index}.bin\",\"id\":\"{index}\"}}"))}]";
        var handler = new RecordingHandler(request =>
        {
            if (request.Method != HttpMethod.Post)
            {
                return Json(HttpStatusCode.OK, "[]");
            }

            return Json(HttpStatusCode.OK, listCalls++ == 0
                ? fullPage
                : """[{"name":"last.bin","id":"last"}]""");
        });
        var sut = CreateSut(handler, SecretKey);

        await sut.DeletePrefixAsync(Bucket, prefix);

        var listRequests = handler.Requests.Where(request => request.Method == HttpMethod.Post).ToArray();
        Assert.Equal(2, listRequests.Length);
        Assert.Contains("\"offset\":0", listRequests[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"offset\":1000", listRequests[1].Body, StringComparison.Ordinal);
        var deleteRequests = handler.Requests.Where(request => request.Method == HttpMethod.Delete).ToArray();
        Assert.Equal(2, deleteRequests.Length);
        Assert.All(deleteRequests, request =>
            Assert.Equal($"/storage/v1/object/{Bucket}", request.Path));

        var deletedKeys = string.Join('\n', deleteRequests.Select(request => request.Body));
        Assert.Contains("tenant-a/file-0.bin", deletedKeys, StringComparison.Ordinal);
        Assert.Contains("tenant-a/last.bin", deletedKeys, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_400_duplicate_json_throws_conflict()
    {
        var handler = new RecordingHandler(Json(
            HttpStatusCode.BadRequest,
            """{"statusCode":"409","error":"Duplicate","message":"The resource already exists"}"""));
        var sut = CreateSut(handler, SecretKey);

        var ex = await Assert.ThrowsAsync<StorageProviderException>(
            () => sut.UploadAsync(Bucket, ObjectKey, new MemoryStream([1]), "text/plain"));

        Assert.Equal(StorageProviderErrorKind.Conflict, ex.Kind);
        Assert.DoesNotContain(SecretKey, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretKey, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_400_invalid_jwt_throws_upstream_not_client()
    {
        var body =
            $$"""{"statusCode":"400","error":"Invalid JWT","code":"UNAUTHORIZED_INVALID_JWT_FORMAT","message":"token {{DummyJwt}} rejected"}""";
        var handler = new RecordingHandler(Json(HttpStatusCode.BadRequest, body));
        var capturing = new CapturingLoggerProvider();
        var sut = CreateSut(handler, DummyJwt, capturing);

        var ex = await Assert.ThrowsAsync<StorageProviderException>(
            () => sut.UploadAsync(Bucket, ObjectKey, new MemoryStream([1]), "text/plain"));

        Assert.Equal(StorageProviderErrorKind.Upstream, ex.Kind);
        Assert.NotEqual(StorageProviderErrorKind.Client, ex.Kind);
        Assert.Equal(StorageProviderException.UpstreamMessage, ex.Message);
        Assert.DoesNotContain(DummyJwt, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(DummyJwt, ex.ToString(), StringComparison.Ordinal);

        var errorLog = Assert.Single(capturing.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("400", errorLog.Message, StringComparison.Ordinal);
        Assert.Contains(Bucket, errorLog.Message, StringComparison.Ordinal);
        Assert.Contains(ObjectKey, errorLog.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(DummyJwt, errorLog.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer ", errorLog.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_400_malformed_body_throws_upstream_not_http_request_exception()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("not-json", Encoding.UTF8, "text/plain"),
        });
        var sut = CreateSut(handler, SecretKey);

        var ex = await Assert.ThrowsAsync<StorageProviderException>(
            () => sut.UploadAsync(Bucket, ObjectKey, new MemoryStream([1]), "text/plain"));

        Assert.Equal(StorageProviderErrorKind.Upstream, ex.Kind);
        Assert.Equal(StorageProviderException.UpstreamMessage, ex.Message);
        Assert.DoesNotContain(SecretKey, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_failure_error_log_includes_bucket_key_status_without_secret()
    {
        var handler = new RecordingHandler(Json(
            HttpStatusCode.BadRequest,
            """{"statusCode":"409","error":"Duplicate","message":"The resource already exists"}"""));
        var capturing = new CapturingLoggerProvider();
        var sut = CreateSut(handler, SecretKey, capturing);

        await Assert.ThrowsAsync<StorageProviderException>(
            () => sut.UploadAsync(Bucket, ObjectKey, new MemoryStream([1]), "text/plain"));

        var errorLog = Assert.Single(capturing.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("400", errorLog.Message, StringComparison.Ordinal);
        Assert.Contains(Bucket, errorLog.Message, StringComparison.Ordinal);
        Assert.Contains(ObjectKey, errorLog.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretKey, errorLog.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Client_maps_to_400_conflict_to_409_upstream_to_502()
    {
        Assert.Equal(
            400,
            StorageProviderActionResults.From(
                new StorageProviderException(
                    StorageProviderErrorKind.Client,
                    StorageProviderException.ClientMessage)).StatusCode);
        Assert.Equal(
            409,
            StorageProviderActionResults.From(
                new StorageProviderException(
                    StorageProviderErrorKind.Conflict,
                    StorageProviderException.ConflictMessage)).StatusCode);
        Assert.Equal(
            502,
            StorageProviderActionResults.From(
                new StorageProviderException(
                    StorageProviderErrorKind.Upstream,
                    StorageProviderException.UpstreamMessage)).StatusCode);
        Assert.Equal(
            500,
            StorageProviderActionResults.FromInvalidOperation(
                new InvalidOperationException(StorageProviderException.NotConfiguredMessage)).StatusCode);
        Assert.Equal(502, StorageProviderActionResults.FromHttpRequestException().StatusCode);
    }

    private static SupabaseStorageProvider CreateSut(
        RecordingHandler handler,
        string serviceRoleKey,
        CapturingLoggerProvider? capturing = null)
    {
        ILogger<SupabaseStorageProvider> logger = capturing is null
            ? Microsoft.Extensions.Logging.Abstractions.NullLogger<SupabaseStorageProvider>.Instance
            : LoggerFactory.Create(builder => builder.AddProvider(capturing))
                .CreateLogger<SupabaseStorageProvider>();

        return new SupabaseStorageProvider(
            new HttpClient(handler),
            Options.Create(new SupabaseOptions
            {
                Url = "https://example.supabase.co",
                JwtSecret = "test-jwt-secret",
                ServiceRoleKey = serviceRoleKey,
            }),
            Options.Create(new StorageOptions()),
            logger);
    }

    private static string? GetApiKey(HttpRequestMessage request)
    {
        return request.Headers.TryGetValues("apikey", out var values)
            ? Assert.Single(values)
            : null;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public RecordingHandler(HttpResponseMessage response)
            : this(_ => response)
        {
        }

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public HttpRequestMessage? Request { get; private set; }

        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri?.AbsolutePath ?? string.Empty,
                request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? string.Empty));
            return Task.FromResult(_respond(request));
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path, string Body);
}
