using Platform.Api.Storage;

namespace Platform.Api.Tests.Fakes;

public sealed class RecordingStorageProvider : IStorageProvider
{
    public List<(string Bucket, string Prefix)> DeletedPrefixes { get; } = [];

    public bool FailPrefixDeletion { get; set; }

    public Task UploadAsync(string bucket, string key, Stream stream, string contentType, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public string GetPublicUrl(string bucket, string key) => $"https://storage.test/{bucket}/{key}";

    public Task<string> CreateSignedUrlAsync(string bucket, string key, TimeSpan ttl, CancellationToken cancellationToken = default) =>
        Task.FromResult(GetPublicUrl(bucket, key));

    public Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task DeletePrefixAsync(string bucket, string prefix, CancellationToken cancellationToken = default)
    {
        DeletedPrefixes.Add((bucket, prefix));
        return FailPrefixDeletion
            ? Task.FromException(new InvalidOperationException("Storage unavailable."))
            : Task.CompletedTask;
    }
}
