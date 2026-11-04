namespace Platform.Api.Storage;

public interface IStorageProvider
{
    Task UploadAsync(
        string bucket,
        string key,
        Stream stream,
        string contentType,
        CancellationToken cancellationToken = default);

    string GetPublicUrl(string bucket, string key);

    Task<string> CreateSignedUrlAsync(
        string bucket,
        string key,
        TimeSpan ttl,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string bucket,
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>Server-side copy (may cross buckets); overwrites the destination.</summary>
    Task CopyAsync(
        string sourceBucket,
        string sourceKey,
        string destinationBucket,
        string destinationKey,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Storage copy is not supported by this provider.");

    Task DeletePrefixAsync(
        string bucket,
        string prefix,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Storage prefix deletion is not supported by this provider.");
}
