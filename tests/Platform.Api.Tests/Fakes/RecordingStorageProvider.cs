using Platform.Api.Storage;

namespace Platform.Api.Tests.Fakes;

public sealed class RecordingStorageProvider : IStorageProvider
{
    public List<(string Bucket, string Prefix)> DeletedPrefixes { get; } = [];

    public bool FailPrefixDeletion { get; set; }

    public Task UploadAsync(string bucket, string key, Stream stream, string contentType, CancellationToken cancellationToken = default)
    {
        Uploads.Add((bucket, key));
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

    public bool FailCopy { get; set; }

    /// <summary>When set, a copy whose destination bucket equals this value fails (e.g. a restore).</summary>
    public string? FailCopyToBucket { get; set; }

    public bool FailRequiredDelete { get; set; }

    /// <summary>When set, required deletes succeed for the first N calls and fail afterwards.</summary>
    public int? FailRequiredDeleteAfter { get; set; }

    private int _requiredDeletes;

    public Task DeleteRequiredAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        if (FailRequiredDelete) return Task.FromException(new InvalidOperationException("Storage unavailable."));
        if (FailRequiredDeleteAfter is int n && _requiredDeletes++ >= n)
            return Task.FromException(new InvalidOperationException("Storage unavailable."));
        Deletes.Add((bucket, key));
        return Task.CompletedTask;
    }

    public Task CopyAsync(string sourceBucket, string sourceKey, string destinationBucket, string destinationKey, CancellationToken cancellationToken = default)
    {
        if (FailCopy || destinationBucket == FailCopyToBucket)
            return Task.FromException(new InvalidOperationException("Storage unavailable."));
        Copies.Add((sourceBucket, sourceKey, destinationBucket, destinationKey));
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        Deletes.Add((bucket, key));
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
