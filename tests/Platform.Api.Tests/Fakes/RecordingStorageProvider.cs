using Platform.Api.Storage;

namespace Platform.Api.Tests.Fakes;

public sealed class RecordingStorageProvider : IStorageProvider
{
    public List<(string Bucket, string Prefix)> DeletedPrefixes { get; } = [];

    public bool FailPrefixDeletion { get; set; }

    public Task UploadAsync(string bucket, string key, Stream stream, string contentType, CancellationToken cancellationToken = default)
    {
        Uploads.Add((bucket, key));
        Objects.Add((bucket, key));
        return Task.CompletedTask;
    }

    public string GetPublicUrl(string bucket, string key) => $"https://storage.test/{bucket}/{key}";

    public Task<string> CreateSignedUrlAsync(string bucket, string key, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        Signed.Add((bucket, key));
        return Task.FromResult($"https://storage.test/sign/{bucket}/{key}");
    }

    public List<(string Bucket, string Key)> Uploads { get; } = [];

    public List<(string FromBucket, string FromKey, string ToBucket, string ToKey)> Copies { get; } = [];

    public List<(string Bucket, string Key)> Deletes { get; } = [];

    public List<(string Bucket, string Key)> Signed { get; } = [];

    public HashSet<(string Bucket, string Key)> Objects { get; } = [];

    public bool FailRequiredDelete { get; set; }

    public Task DeleteRequiredAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        if (FailRequiredDelete) return Task.FromException(new InvalidOperationException("Storage unavailable."));
        return DeleteAsync(bucket, key, cancellationToken);
    }

    public Task CopyAsync(string sourceBucket, string sourceKey, string destinationBucket, string destinationKey, CancellationToken cancellationToken = default)
    {
        Copies.Add((sourceBucket, sourceKey, destinationBucket, destinationKey));
        Objects.Add((destinationBucket, destinationKey));
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        Deletes.Add((bucket, key));
        Objects.Remove((bucket, key));
        return Task.CompletedTask;
    }

    public Task DeletePrefixAsync(string bucket, string prefix, CancellationToken cancellationToken = default)
    {
        DeletedPrefixes.Add((bucket, prefix));
        return FailPrefixDeletion
            ? Task.FromException(new InvalidOperationException("Storage unavailable."))
            : Task.CompletedTask;
    }
}
