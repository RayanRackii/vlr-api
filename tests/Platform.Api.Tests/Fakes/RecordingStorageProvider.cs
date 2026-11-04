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

    public bool FailCopy { get; set; }

    public bool FailAfterCopy { get; set; }

    public HashSet<(string Bucket, string Key)> Objects { get; } = [];

    /// <summary>When set, a copy whose destination bucket equals this value fails (e.g. a restore).</summary>
    public string? FailCopyToBucket { get; set; }

    public bool FailRequiredDelete { get; set; }

    /// <summary>When set, required deletes succeed for the first N calls and fail afterwards.</summary>
    public int? FailRequiredDeleteAfter { get; set; }

    public string? FailRequiredDeleteInBucket { get; set; }

    /// <summary>When set, a required delete removes the object (server-side success) but the call
    /// then throws, modelling a timeout whose outcome is unknown to the caller.</summary>
    public string? TimeoutAfterRequiredDeleteInBucket { get; set; }

    private int _requiredDeletes;

    public Task<bool> ExistsAsync(string bucket, string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(Objects.Contains((bucket, key)));

    public Task DeleteRequiredAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        if (FailRequiredDelete && (FailRequiredDeleteInBucket is null || bucket == FailRequiredDeleteInBucket)) return Task.FromException(new InvalidOperationException("Storage unavailable."));
        if ((FailRequiredDeleteInBucket is null || bucket == FailRequiredDeleteInBucket)
            && FailRequiredDeleteAfter is int n && _requiredDeletes++ >= n)
            return Task.FromException(new InvalidOperationException("Storage unavailable."));
        Deletes.Add((bucket, key));
        Objects.Remove((bucket, key));
        if (bucket == TimeoutAfterRequiredDeleteInBucket)
            return Task.FromException(new TimeoutException("Delete timed out after storage removed the object."));
        return Task.CompletedTask;
    }

    public Task CopyAsync(string sourceBucket, string sourceKey, string destinationBucket, string destinationKey, CancellationToken cancellationToken = default)
    {
        if (FailCopy || destinationBucket == FailCopyToBucket)
            return Task.FromException(new InvalidOperationException("Storage unavailable."));
        Copies.Add((sourceBucket, sourceKey, destinationBucket, destinationKey));
        Objects.Add((destinationBucket, destinationKey));
        if (FailAfterCopy) return Task.FromException(new TimeoutException("Copy timed out after storage accepted the object."));
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
