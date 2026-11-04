using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Storage;
using Platform.Api.Tests.Fakes;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Tests.Catalog;

public sealed class CatalogStorageListingTests
{
    [Fact]
    public async Task Supabase_listing_recurses_from_bucket_root_and_paginates_without_mutations()
    {
        var calls = new List<(string Prefix, int Offset)>();
        using var client = new HttpClient(new ListingHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/storage/v1/object/list/public", request.RequestUri!.AbsolutePath);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var prefix = document.RootElement.GetProperty("prefix").GetString()!;
            var offset = document.RootElement.GetProperty("offset").GetInt32();
            Assert.Equal(1000, document.RootElement.GetProperty("limit").GetInt32());
            calls.Add((prefix, offset));
            object[] entries = prefix switch
            {
                "" => [new { name = "tenant", id = (string?)null }],
                "tenant/" => [new { name = "product", id = (string?)null }],
                "tenant/product/" when offset == 0 => Enumerable.Range(0, 1000)
                    .Select(i => (object)new { name = $"file-{i}", id = $"object-{i}" }).ToArray(),
                "tenant/product/" when offset == 1000 => [new { name = "last", id = "last-object" }],
                _ => throw new InvalidOperationException("Unexpected listing request.")
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(entries) };
        }));
        var storage = new SupabaseStorageProvider(client,
            Options.Create(new SupabaseOptions { Url = "https://storage.test", ServiceRoleKey = "sb_secret_test" }),
            Options.Create(new StorageOptions()), NullLogger<SupabaseStorageProvider>.Instance);
        var keys = await storage.ListAsync("public", "");
        Assert.Equal(1001, keys.Count);
        Assert.Contains("tenant/product/last", keys);
        Assert.Contains("tenant/product/file-999", keys);
        Assert.Equal(new[] { ("", 0), ("tenant/", 0), ("tenant/product/", 0), ("tenant/product/", 1000) }, calls);
    }

    [Fact]
    public async Task Dev_listing_reads_recursive_files_and_respects_bucket_and_prefix()
    {
        var root = Path.Combine(Path.GetTempPath(), $"catalog-list-{Guid.NewGuid():N}");
        try
        {
            var storage = new DevStorageProvider(new FakeHostEnvironment { ContentRootPath = root }, Options.Create(new StorageOptions()));
            foreach (var (bucket, key) in new[] { ("public", "tenant/product/file"), ("public", "other/product/file"), ("private", "tenant/product/secret") })
                await storage.UploadAsync(bucket, key, new MemoryStream([1]), "image/png");
            Assert.Equal(new[] { "other/product/file", "tenant/product/file" }, await storage.ListAsync("public", ""));
            Assert.Equal(new[] { "tenant/product/file" }, await storage.ListAsync("public", "tenant/"));
            Assert.Empty(await storage.ListAsync("missing", ""));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ListingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
